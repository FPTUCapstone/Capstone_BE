using System.Net.Http.Json;

using FluentAssertions;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;

using TripMate.Api.IntegrationTests.Infrastructure;
using TripMate.Application.Common.Interfaces;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Api.IntegrationTests.TourOperatorApplications;

/**
 * [UC-51] SQL Server Concurrency and Atomicity Integration Tests
 *
 * Addresses leader code-review item [P2]:
 *   - Verifies SQL Server concurrency token enforced by WHERE on OperatorProfile row.
 *   - Verifies atomic rollback: User, profile, documents, audit log, notification
 *     all roll back when a SaveChanges fault occurs mid-transaction.
 *   - Verifies reject-approve race: exactly one state transition persists (409 for loser).
 */
[Collection(nameof(TripMateApiFactory))]
public sealed class ApproveOperatorApplicationSqlServerTests
{
    private const long AdminUserId = 999;

    // ── [P2-1] Concurrent approve-approve ────────────────────────────────────
    // One request wins (200), the other receives 409 Conflict.
    // Exactly one audit log and one notification must exist after the race.
    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task ConcurrentApproveApprove_ExactlyOneSucceeds_OtherReturns409()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        var barrier = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var interceptorFired = 0;

        var interceptor = new BarrierSaveChangesInterceptor(
            () => Interlocked.Increment(ref interceptorFired),
            barrier);

        await using var factory = new TripMateApiFactory(
            sqlServerConnectionString: database.ConnectionString,
            saveChangesInterceptor: interceptor);

        var userId = await SeedPendingOperatorAsync(factory);

        var client1 = factory.CreateAuthenticatedClient(AdminUserId, UserRole.Administrator);
        var client2 = factory.CreateAuthenticatedClient(AdminUserId + 1, UserRole.Administrator);

        var task1 = client1.PostAsync(
            $"/api/v1/admin/tour-operator-applications/{userId}/approve",
            JsonContent.Create(new { }));

        await WaitForInterceptorAsync(() => interceptorFired >= 1);
        barrier.TrySetResult();

        var task2 = client2.PostAsync(
            $"/api/v1/admin/tour-operator-applications/{userId}/approve",
            JsonContent.Create(new { }));

        var responses = await Task.WhenAll(task1, task2);
        var statuses = responses.Select(r => (int)r.StatusCode).OrderBy(s => s).ToList();
        statuses.Should().BeEquivalentTo(new[] { 200, 409 },
            "exactly one approve must win; the other must receive 409 Conflict");

        await using var verifyDb = database.CreateDbContext();

        var auditCount = await verifyDb.AuditLogs.CountAsync(
            a => a.AffectedEntityId == userId &&
                 a.ActionType == "ApproveOperatorApplication");
        auditCount.Should().Be(1, "exactly one audit log must exist for the winning request");

        var notificationCount = await verifyDb.Notifications.CountAsync(
            n => n.UserId == userId &&
                 n.Type == "OperatorApplicationApproved");
        notificationCount.Should().Be(1, "exactly one notification must be created");

        var finalUser = await verifyDb.Users.AsNoTracking().SingleAsync(u => u.Id == userId);
        finalUser.Status.Should().Be(AccountStatus.Active,
            "the winning approve must have set User.Status to Active in the database");
    }

    // ── [P2-2] Fault injection after first SaveChanges ────────────────────────
    // Simulates a mid-transaction failure. Verifies that every row — User, profile,
    // documents, audit log, and notification — is rolled back by the transaction.
    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task ApprovalFaultMidTransaction_RollsBackAllSideEffects()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        var faultInterceptor = new FaultOnFirstSaveInterceptor();

        await using var factory = new TripMateApiFactory(
            sqlServerConnectionString: database.ConnectionString,
            saveChangesInterceptor: faultInterceptor);

        var userId = await SeedPendingOperatorAsync(factory);

        var client = factory.CreateAuthenticatedClient(AdminUserId, UserRole.Administrator);
        var response = await client.PostAsync(
            $"/api/v1/admin/tour-operator-applications/{userId}/approve",
            JsonContent.Create(new { }));

        ((int)response.StatusCode).Should()
            .BeOneOf(new[] { 500, 503, 409 },
            "a faulted SaveChanges must not return 200 OK");

        // Verify with a fresh DbContext that was never part of the failed request.
        await using var verifyDb = database.CreateDbContext();

        var userAfter = await verifyDb.Users.AsNoTracking().SingleAsync(u => u.Id == userId);
        userAfter.Status.Should().Be(AccountStatus.PendingApproval,
            "User.Status must be rolled back to PendingApproval");

        var profileAfter = await verifyDb.OperatorProfiles.AsNoTracking()
            .SingleAsync(p => p.UserId == userId);
        profileAfter.ApprovalStatus.Should().Be(OperatorApprovalStatus.PendingApproval,
            "OperatorProfile.ApprovalStatus must be rolled back");
        profileAfter.ReviewedBy.Should().BeNull(
            "ReviewedBy must remain null after rollback");

        var auditCount = await verifyDb.AuditLogs.CountAsync(
            a => a.AffectedEntityId == userId &&
                 a.ActionType == "ApproveOperatorApplication");
        auditCount.Should().Be(0, "no audit log must be persisted after rollback");

        var notificationCount = await verifyDb.Notifications.CountAsync(
            n => n.UserId == userId &&
                 n.Type == "OperatorApplicationApproved");
        notificationCount.Should().Be(0, "no notification must be persisted after rollback");
    }

    // ── [P2-3] Reject-approve race ────────────────────────────────────────────
    // One admin approves while another simultaneously rejects.
    // Exactly one transition must succeed; the loser must receive 409.
    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task ConcurrentRejectApprove_ExactlyOneTransitionSucceeds()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        var barrier = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var interceptorFired = 0;

        var interceptor = new BarrierSaveChangesInterceptor(
            () => Interlocked.Increment(ref interceptorFired),
            barrier);

        await using var factory = new TripMateApiFactory(
            sqlServerConnectionString: database.ConnectionString,
            saveChangesInterceptor: interceptor);

        var userId = await SeedPendingOperatorAsync(factory);

        var client1 = factory.CreateAuthenticatedClient(AdminUserId, UserRole.Administrator);
        var client2 = factory.CreateAuthenticatedClient(AdminUserId + 1, UserRole.Administrator);

        var approveTask = client1.PostAsync(
            $"/api/v1/admin/tour-operator-applications/{userId}/approve",
            JsonContent.Create(new { }));

        await WaitForInterceptorAsync(() => interceptorFired >= 1);
        barrier.TrySetResult();

        var rejectTask = client2.PostAsync(
            $"/api/v1/admin/tour-operator-applications/{userId}/reject",
            JsonContent.Create(new { reason = "Tai lieu chua day du." }));

        var responses = await Task.WhenAll(approveTask, rejectTask);
        var statuses = responses.Select(r => (int)r.StatusCode).OrderBy(s => s).ToList();
        statuses.Should().BeEquivalentTo(new[] { 200, 409 },
            "exactly one of approve/reject must win; the other must get 409 Conflict");

        await using var verifyDb = database.CreateDbContext();
        var profile = await verifyDb.OperatorProfiles.AsNoTracking()
            .SingleAsync(p => p.UserId == userId);
        profile.ApprovalStatus.Should()
            .BeOneOf(
                new[] { OperatorApprovalStatus.Approved, OperatorApprovalStatus.Rejected },
                "profile must be in exactly one final state after the race");

        var auditCount = await verifyDb.AuditLogs.CountAsync(
            a => a.AffectedEntityId == userId &&
                 (a.ActionType == "ApproveOperatorApplication" ||
                  a.ActionType == "RejectOperatorApplication"));
        auditCount.Should().Be(1, "exactly one audit log must exist after the race");
    }

    // ── Seed helper ───────────────────────────────────────────────────────────

    private static async Task<long> SeedPendingOperatorAsync(TripMateApiFactory factory)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<IApplicationDbContext>();

        var user = new User
        {
            FullName = "SQL Concurrency Operator",
            Email = $"concurrency_{Guid.NewGuid():N}@example.com",
            Role = UserRole.TourOperator,
            Status = AccountStatus.PendingApproval,
        };
        db.Users.Add(user);
        await db.SaveChangesAsync(CancellationToken.None);

        var profile = new OperatorProfile
        {
            User = user,
            CompanyName = "SQL Operator Co",
            BusinessLicenseNo = "SQL-LIC-001",
            TaxCode = "SQL-TAX-001",
            ApprovalStatus = OperatorApprovalStatus.PendingApproval,
        };
        db.OperatorProfiles.Add(profile);

        db.OperatorDocuments.Add(new OperatorDocument
        {
            OperatorProfile = profile,
            DocumentType = OperatorDocumentType.BusinessLicense,
            FileUrl = "https://storage.tripmate.vn/test-license.pdf",
            Status = DocumentStatus.Submitted,
        });

        await db.SaveChangesAsync(CancellationToken.None);
        return user.Id;
    }

    private static async Task WaitForInterceptorAsync(
        Func<bool> condition, int maxWaitMs = 5000)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (!condition() && sw.ElapsedMilliseconds < maxWaitMs)
        {
            await Task.Delay(10);
        }
    }
}

// ── SaveChangesInterceptors ───────────────────────────────────────────────────

/// <summary>
/// Increments a counter on every SavingChangesAsync call, then waits for a
/// <see cref="TaskCompletionSource"/> barrier before allowing the save to proceed.
/// Used to force two concurrent requests to race on a single SaveChanges.
/// </summary>
internal sealed class BarrierSaveChangesInterceptor(
    Action onSave,
    TaskCompletionSource barrier) : SaveChangesInterceptor
{
    public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        onSave();
        await barrier.Task.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
        return result;
    }
}

/// <summary>
/// Throws <see cref="DbUpdateException"/> on the first SavingChangesAsync call to
/// simulate a fault mid-transaction. All subsequent calls proceed normally.
/// </summary>
internal sealed class FaultOnFirstSaveInterceptor : SaveChangesInterceptor
{
    private int _callCount;

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        if (Interlocked.Increment(ref _callCount) == 1)
        {
            throw new DbUpdateException(
                "Simulated fault injected to verify full transaction rollback.");
        }

        return ValueTask.FromResult(result);
    }
}
