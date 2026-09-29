using System.Net;
using System.Net.Http.Json;

using FluentAssertions;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

using TripMate.Api.IntegrationTests.Infrastructure;
using TripMate.Domain.Common;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Api.IntegrationTests.Admin;

/// <summary>
/// Exercises UC-51 against SQL Server because EF InMemory cannot prove the
/// concurrency-token WHERE predicate or relational transaction rollback.
/// </summary>
[Collection(nameof(TripMateApiFactory))]
public sealed class RejectTourOperatorApplicationSqlServerTests
{
    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task ConcurrentRejectRequests_OneSucceedsOneConflicts_AndCreatesOneDecision()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        var seed = await SeedPendingApplicationAsync(database);
        var barrier = new OperatorDecisionSaveBarrier(2);
        await using var factory = new TripMateApiFactory(
            sqlServerConnectionString: database.ConnectionString,
            saveChangesInterceptor: barrier);
        using var firstClient = factory.CreateAuthenticatedClient(seed.AdminId, UserRole.Administrator);
        using var secondClient = factory.CreateAuthenticatedClient(seed.AdminId, UserRole.Administrator);

        var first = firstClient.PostAsJsonAsync(
            $"/api/v1/admin/tour-operator-applications/{seed.OperatorId}/reject",
            new { reason = "The first concurrent rejection reason." });
        var second = secondClient.PostAsJsonAsync(
            $"/api/v1/admin/tour-operator-applications/{seed.OperatorId}/reject",
            new { reason = "The second concurrent rejection reason." });

        var responses = await Task.WhenAll(first, second);

        responses.Select(response => response.StatusCode).Should().BeEquivalentTo(
            [HttpStatusCode.OK, HttpStatusCode.Conflict]);
        barrier.Arrivals.Should().Be(2, "both requests must load PendingApproval before either save proceeds");

        await using var verification = database.CreateDbContext();
        var user = await verification.Users.AsNoTracking().SingleAsync(item => item.Id == seed.OperatorId);
        var profile = await verification.OperatorProfiles.AsNoTracking()
            .SingleAsync(item => item.UserId == seed.OperatorId);
        var document = await verification.OperatorDocuments.AsNoTracking()
            .SingleAsync(item => item.OperatorUserId == seed.OperatorId);
        var audits = await verification.AuditLogs.AsNoTracking()
            .Where(item => item.AffectedEntityId == seed.OperatorId &&
                item.ActionType == AuditActionTypes.OperatorApplicationReject)
            .ToListAsync();
        var notifications = await verification.Notifications.AsNoTracking()
            .Where(item => item.UserId == seed.OperatorId &&
                item.Type == "OperatorApplicationRejected")
            .ToListAsync();

        user.Status.Should().Be(AccountStatus.Rejected);
        profile.ApprovalStatus.Should().Be(OperatorApprovalStatus.Rejected);
        profile.RejectionReason.Should().BeOneOf(
            "The first concurrent rejection reason.",
            "The second concurrent rejection reason.");
        document.Status.Should().Be(DocumentStatus.Rejected);
        audits.Should().ContainSingle(item => item.Result == AuditOutcome.Success);
        audits.Should().ContainSingle(item => item.Result == AuditOutcome.Failure);
        notifications.Should().ContainSingle();
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task ConcurrentRejectAndApprove_OneSucceedsOneConflicts_AndPersistsOneConsistentDecision()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        var seed = await SeedPendingApplicationAsync(database);
        var barrier = new OperatorDecisionSaveBarrier(2);
        await using var factory = new TripMateApiFactory(
            sqlServerConnectionString: database.ConnectionString,
            saveChangesInterceptor: barrier);
        using var rejectClient = factory.CreateAuthenticatedClient(seed.AdminId, UserRole.Administrator);
        using var approveClient = factory.CreateAuthenticatedClient(seed.AdminId, UserRole.Administrator);

        var reject = rejectClient.PostAsJsonAsync(
            $"/api/v1/admin/tour-operator-applications/{seed.OperatorId}/reject",
            new { reason = "The licence could not be verified." });
        var approve = approveClient.PostAsJsonAsync(
            $"/api/v1/admin/tour-operator-applications/{seed.OperatorId}/approve",
            new { });

        var responses = await Task.WhenAll(reject, approve);

        responses.Select(response => response.StatusCode).Should().BeEquivalentTo(
            [HttpStatusCode.OK, HttpStatusCode.Conflict]);
        barrier.Arrivals.Should().Be(2, "both decisions must race from the same PendingApproval state");

        await using var verification = database.CreateDbContext();
        var user = await verification.Users.AsNoTracking().SingleAsync(item => item.Id == seed.OperatorId);
        var profile = await verification.OperatorProfiles.AsNoTracking()
            .SingleAsync(item => item.UserId == seed.OperatorId);
        var document = await verification.OperatorDocuments.AsNoTracking()
            .SingleAsync(item => item.OperatorUserId == seed.OperatorId);
        var audits = await verification.AuditLogs.AsNoTracking()
            .Where(item => item.AffectedEntityId == seed.OperatorId &&
                (item.ActionType == AuditActionTypes.OperatorApplicationReject ||
                 item.ActionType == AuditActionTypes.OperatorApplicationApprove))
            .ToListAsync();
        var notifications = await verification.Notifications.AsNoTracking()
            .Where(item => item.UserId == seed.OperatorId &&
                (item.Type == "OperatorApplicationRejected" ||
                 item.Type == "OperatorApplicationApproved"))
            .ToListAsync();

        var audit = audits.Should()
            .ContainSingle(item => item.Result == AuditOutcome.Success)
            .Subject;
        audits.Should().ContainSingle(item => item.Result == AuditOutcome.Failure);
        var notification = notifications.Should().ContainSingle().Subject;
        if (profile.ApprovalStatus == OperatorApprovalStatus.Rejected)
        {
            user.Status.Should().Be(AccountStatus.Rejected);
            document.Status.Should().Be(DocumentStatus.Rejected);
            audit.ActionType.Should().Be(AuditActionTypes.OperatorApplicationReject);
            notification.Type.Should().Be("OperatorApplicationRejected");
        }
        else
        {
            profile.ApprovalStatus.Should().Be(OperatorApprovalStatus.Approved);
            user.Status.Should().Be(AccountStatus.Active);
            document.Status.Should().Be(DocumentStatus.Approved);
            audit.ActionType.Should().Be(AuditActionTypes.OperatorApplicationApprove);
            notification.Type.Should().Be("OperatorApplicationApproved");
        }
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task Reject_WhenDatabaseFailsAfterProfileUpdate_RollsBackEverySideEffect()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        var seed = await SeedPendingApplicationAsync(database);
        await database.ExecuteNonQueryAsync("""
            CREATE TRIGGER dbo.TR_UC51_RejectOperatorProfileUpdate
            ON dbo.OperatorProfiles
            AFTER UPDATE
            AS
            BEGIN
                SET NOCOUNT ON;
                IF EXISTS (SELECT 1 FROM inserted WHERE approval_status = 'Rejected')
                    THROW 51051, 'UC51 injected failure after OperatorProfiles update.', 1;
            END;
            """);
        await using var factory = new TripMateApiFactory(
            sqlServerConnectionString: database.ConnectionString);
        using var client = factory.CreateAuthenticatedClient(seed.AdminId, UserRole.Administrator);

        var response = await client.PostAsJsonAsync(
            $"/api/v1/admin/tour-operator-applications/{seed.OperatorId}/reject",
            new { reason = "This decision must roll back." });

        response.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
        var responseBody = await response.Content.ReadAsStringAsync();
        responseBody.Should().NotContain("TR_UC51");
        responseBody.Should().NotContain("injected failure");

        await using var verification = database.CreateDbContext();
        var user = await verification.Users.AsNoTracking().SingleAsync(item => item.Id == seed.OperatorId);
        var profile = await verification.OperatorProfiles.AsNoTracking()
            .SingleAsync(item => item.UserId == seed.OperatorId);
        var document = await verification.OperatorDocuments.AsNoTracking()
            .SingleAsync(item => item.OperatorUserId == seed.OperatorId);

        user.Status.Should().Be(AccountStatus.PendingApproval);
        profile.ApprovalStatus.Should().Be(OperatorApprovalStatus.PendingApproval);
        profile.RejectionReason.Should().BeNull();
        profile.ReviewedBy.Should().BeNull();
        profile.ReviewedAtUtc.Should().BeNull();
        document.Status.Should().Be(DocumentStatus.Submitted);
        var audits = await verification.AuditLogs.AsNoTracking()
            .Where(item => item.AffectedEntityId == seed.OperatorId &&
                item.ActionType == AuditActionTypes.OperatorApplicationReject)
            .ToListAsync();
        audits.Should().NotContain(item => item.Result == AuditOutcome.Success);
        audits.Should().ContainSingle(item => item.Result == AuditOutcome.Failure);
        (await verification.Notifications.AsNoTracking()
            .CountAsync(item => item.UserId == seed.OperatorId &&
                item.Type == "OperatorApplicationRejected"))
            .Should().Be(0);
    }

    private static async Task<SeedResult> SeedPendingApplicationAsync(SqlServerTestDatabase database)
    {
        await using var context = database.CreateDbContext();
        var suffix = Guid.NewGuid().ToString("N");
        var now = new DateTimeOffset(2026, 9, 29, 0, 0, 0, TimeSpan.Zero);
        var admin = new User
        {
            Role = UserRole.Administrator,
            Email = $"uc51-admin-{suffix}@tripmate.local",
            FullName = "UC-51 SQL Administrator",
            Status = AccountStatus.Active,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        };
        var tourOperator = new User
        {
            Role = UserRole.TourOperator,
            Email = $"uc51-operator-{suffix}@tripmate.local",
            FullName = "UC-51 SQL Tour Operator",
            Status = AccountStatus.PendingApproval,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        };
        context.Users.AddRange(admin, tourOperator);
        await context.SaveChangesAsync();

        context.OperatorProfiles.Add(new OperatorProfile
        {
            UserId = tourOperator.Id,
            CompanyName = "UC-51 SQL Operator",
            TaxCode = $"T{suffix[..20]}",
            BusinessLicenseNo = $"BL-{suffix}",
            ApprovalStatus = OperatorApprovalStatus.PendingApproval,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        });
        context.OperatorDocuments.Add(new OperatorDocument
        {
            OperatorUserId = tourOperator.Id,
            DocumentType = OperatorDocumentType.BusinessLicense,
            FileUrl = "https://storage.tripmate.local/uc51/business-license.pdf",
            Status = DocumentStatus.Submitted,
            UploadedAtUtc = now,
        });
        await context.SaveChangesAsync();

        return new SeedResult(admin.Id, tourOperator.Id);
    }

    private sealed record SeedResult(long AdminId, long OperatorId);

    private sealed class OperatorDecisionSaveBarrier(int participants) : SaveChangesInterceptor
    {
        private readonly TaskCompletionSource<bool> _release =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _arrivals;

        public int Arrivals => Volatile.Read(ref _arrivals);

        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            var isOperatorDecision = eventData.Context?.ChangeTracker
                .Entries<OperatorProfile>()
                .Any(entry => entry.State == EntityState.Modified &&
                    entry.Property(profile => profile.ApprovalStatus).IsModified) == true;

            if (!isOperatorDecision)
            {
                return result;
            }

            if (Interlocked.Increment(ref _arrivals) == participants)
            {
                _release.TrySetResult(true);
            }

            await _release.Task.WaitAsync(TimeSpan.FromSeconds(15), cancellationToken);
            return result;
        }
    }
}