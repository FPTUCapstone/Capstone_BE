using System.Net;
using System.Net.Http.Json;

using FluentAssertions;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

using TripMate.Api.IntegrationTests.Infrastructure;
using TripMate.Domain.Common;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Api.IntegrationTests.Payouts;

/// <summary>
/// Exercises UC-73/UC-74 (I02) against SQL Server because EF InMemory cannot prove the
/// Payout.Status concurrency-token WHERE predicate or the all-or-nothing rollback of the audit
/// row alongside the state change. Handler-level unit tests cover the business branches with a
/// simulated <see cref="DbUpdateConcurrencyException"/>; this proves the real race. Also covers
/// the request-side race on the payout_code placeholder (two operators requesting at the same
/// instant must not collide on a shared "PENDING" value — see RequestPayoutCommandHandler).
/// </summary>
[Collection(nameof(TripMateApiFactory))]
public sealed class PayoutReviewConcurrencySqlServerTests
{
    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task ConcurrentApproveRequests_OneSucceedsOneConflicts_AndCreatesOneAuditRow()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        var seed = await SeedPendingReviewPayoutAsync(database);
        var barrier = new PayoutDecisionSaveBarrier(2);
        await using var factory = new TripMateApiFactory(
            sqlServerConnectionString: database.ConnectionString,
            saveChangesInterceptor: barrier);
        using var firstClient = factory.CreateAuthenticatedClient(seed.StaffId, UserRole.Staff);
        using var secondClient = factory.CreateAuthenticatedClient(seed.StaffId, UserRole.Staff);

        var first = firstClient.PostAsync(
            $"/api/v1/admin/payouts/{seed.PayoutId}/approve", content: null);
        var second = secondClient.PostAsync(
            $"/api/v1/admin/payouts/{seed.PayoutId}/approve", content: null);

        var responses = await Task.WhenAll(first, second);

        responses.Select(response => response.StatusCode).Should().BeEquivalentTo(
            [HttpStatusCode.OK, HttpStatusCode.Conflict]);
        barrier.Arrivals.Should().Be(2, "both requests must load PendingReview before either save proceeds");

        var conflict = responses.Single(response => response.StatusCode == HttpStatusCode.Conflict);
        var conflictBody = await conflict.Content.ReadAsStringAsync();
        conflictBody.Should().Contain("ApprovedForSettlement", "the loser must see the winner's committed status");

        await using var verification = database.CreateDbContext();
        var payout = await verification.Payouts.AsNoTracking()
            .SingleAsync(item => item.Id == seed.PayoutId);
        payout.Status.Should().Be(Payout.StatusApprovedForSettlement, "exactly one approval committed");

        var audits = await verification.AuditLogs.AsNoTracking()
            .Where(item => item.AffectedEntityId == seed.PayoutId
                && item.ActionType == AuditActionTypes.PayoutApproved)
            .ToListAsync();
        audits.Should().ContainSingle(
            "the losing request's SaveChangesAsync must roll back its own audit insert along with the state change");
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task ConcurrentApproveAndRejectRequests_OneSucceedsOneConflicts()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        var seed = await SeedPendingReviewPayoutAsync(database);
        var barrier = new PayoutDecisionSaveBarrier(2);
        await using var factory = new TripMateApiFactory(
            sqlServerConnectionString: database.ConnectionString,
            saveChangesInterceptor: barrier);
        using var approveClient = factory.CreateAuthenticatedClient(seed.StaffId, UserRole.Staff);
        using var rejectClient = factory.CreateAuthenticatedClient(seed.StaffId, UserRole.Staff);

        var approve = approveClient.PostAsync($"/api/v1/admin/payouts/{seed.PayoutId}/approve", content: null);
        var reject = rejectClient.PostAsJsonAsync(
            $"/api/v1/admin/payouts/{seed.PayoutId}/reject",
            new { reason = "Bank details could not be verified for this period." });

        var responses = await Task.WhenAll(approve, reject);

        responses.Select(response => response.StatusCode).Should().BeEquivalentTo(
            [HttpStatusCode.OK, HttpStatusCode.Conflict]);
        barrier.Arrivals.Should().Be(2);

        await using var verification = database.CreateDbContext();
        var payout = await verification.Payouts.AsNoTracking()
            .SingleAsync(item => item.Id == seed.PayoutId);
        payout.Status.Should().BeOneOf(Payout.StatusApprovedForSettlement, Payout.StatusRejected);

        // Exactly one of the two decision types must be recorded, matching whichever status won.
        var auditActionTypes = await verification.AuditLogs.AsNoTracking()
            .Where(item => item.AffectedEntityId == seed.PayoutId
                && (item.ActionType == AuditActionTypes.PayoutApproved
                    || item.ActionType == AuditActionTypes.PayoutRejected))
            .Select(item => item.ActionType)
            .ToListAsync();
        auditActionTypes.Should().ContainSingle();
        if (payout.Status == Payout.StatusApprovedForSettlement)
        {
            auditActionTypes.Single().Should().Be(AuditActionTypes.PayoutApproved);
            payout.RejectionReason.Should().BeNull();
        }
        else
        {
            auditActionTypes.Single().Should().Be(AuditActionTypes.PayoutRejected);
            payout.RejectionReason.Should().Be("Bank details could not be verified for this period.");
        }
    }

    /// <summary>
    /// Reproduces the race this PR's review flagged in RequestPayoutCommandHandler: before the
    /// fix, every in-flight request inserted its header row with the same literal payout_code
    /// placeholder ("PENDING"), which collides on the UX_Payouts_Code unique index regardless of
    /// which operator is requesting. The fix uses a per-request unique placeholder (TMP-{guid}),
    /// so two concurrent inserts that reach SaveChangesAsync before either has a real code must
    /// not collide on that placeholder. Exercised directly against Payout.Create + SaveChanges —
    /// not through the HTTP endpoint — so this is a focused regression guard for the placeholder
    /// fix, not a re-test of the eligibility/aggregation logic RequestPayoutCommandHandlerTests
    /// already covers via InMemory.
    /// </summary>
    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task ConcurrentPayoutInserts_DoNotCollideOnThePlaceholderCode()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        await database.ExecuteNonQueryAsync("""
            DECLARE @opA BIGINT, @opB BIGINT;
            INSERT dbo.Users(role, email, full_name, status)
            VALUES ('TourOperator', N'placeholder-race-a@example.com', N'Placeholder Race A', 'Active');
            SET @opA = SCOPE_IDENTITY();
            INSERT dbo.OperatorProfiles(user_id, company_name, tax_code, business_license_no)
            VALUES (@opA, N'Placeholder Race A', N'TAX-PH-A', N'LIC-PH-A');

            INSERT dbo.Users(role, email, full_name, status)
            VALUES ('TourOperator', N'placeholder-race-b@example.com', N'Placeholder Race B', 'Active');
            SET @opB = SCOPE_IDENTITY();
            INSERT dbo.OperatorProfiles(user_id, company_name, tax_code, business_license_no)
            VALUES (@opB, N'Placeholder Race B', N'TAX-PH-B', N'LIC-PH-B');
            """);
        var operatorAId = await database.ExecuteScalarAsync<long>(
            "SELECT user_id FROM dbo.Users WHERE email = N'placeholder-race-a@example.com'");
        var operatorBId = await database.ExecuteScalarAsync<long>(
            "SELECT user_id FROM dbo.Users WHERE email = N'placeholder-race-b@example.com'");

        static Payout BuildPayout(long operatorId) => Payout.Create(
            payoutCode: $"TMP-{Guid.NewGuid():N}"[..Payout.PayoutCodeMaxLength],
            operatorUserId: operatorId,
            months: [new PayoutMonthTotal(
                new DateOnly(2026, 9, 1), EligibleBookingCount: 1, ParticipantCount: 1,
                PaymentsReceived: 2_000_000m, DiscountsGiven: 0m, RefundedAmount: 0m,
                CommissionAmount: 200_000m, PayableNetAmount: 1_800_000m)],
            cutOffAtUtc: DateTimeOffset.UtcNow,
            adjustmentsApplied: 0m,
            minimumAmountChecked: 1_000_000m,
            bankName: "Vietcombank",
            accountHolderName: "Operator",
            accountNumberMasked: "****1234",
            accountNumberProtected: "protected:1029384756",
            requestedAtUtc: DateTimeOffset.UtcNow);

        await using var dbA = database.CreateDbContext();
        await using var dbB = database.CreateDbContext();
        dbA.Payouts.Add(BuildPayout(operatorAId));
        dbB.Payouts.Add(BuildPayout(operatorBId));

        var insertA = dbA.SaveChangesAsync();
        var insertB = dbB.SaveChangesAsync();
        var act = async () => await Task.WhenAll(insertA, insertB);

        await act.Should().NotThrowAsync(
            "distinct GUID-based placeholders must never collide on UX_Payouts_Code");

        await using var verification = database.CreateDbContext();
        var payoutCodes = await verification.Payouts.AsNoTracking()
            .Where(payout => payout.OperatorUserId == operatorAId || payout.OperatorUserId == operatorBId)
            .Select(payout => payout.PayoutCode)
            .ToListAsync();
        payoutCodes.Should().HaveCount(2);
        payoutCodes.Should().OnlyHaveUniqueItems();
        payoutCodes.Should().NotContain("PENDING");
    }

    private static async Task<SeedResult> SeedPendingReviewPayoutAsync(SqlServerTestDatabase database)
    {
        await database.ExecuteNonQueryAsync("""
            DECLARE @operator BIGINT;
            INSERT dbo.Users(role, email, full_name, status)
            VALUES ('TourOperator', N'concurrency-operator@example.com', N'Concurrency Tours', 'Active');
            SET @operator = SCOPE_IDENTITY();
            INSERT dbo.OperatorProfiles(user_id, company_name, tax_code, business_license_no)
            VALUES (@operator, N'Concurrency Tours', N'TAX-CONC-1', N'LIC-CONC-1');

            DECLARE @staff BIGINT;
            INSERT dbo.Users(role, email, full_name, status)
            VALUES ('Staff', N'concurrency-staff@example.com', N'Concurrency Staff', 'Active');
            SET @staff = SCOPE_IDENTITY();
            """);

        var operatorId = await database.ExecuteScalarAsync<long>(
            "SELECT user_id FROM dbo.Users WHERE email = N'concurrency-operator@example.com'");
        var staffId = await database.ExecuteScalarAsync<long>(
            "SELECT user_id FROM dbo.Users WHERE email = N'concurrency-staff@example.com'");

        await using var db = database.CreateDbContext();
        var payout = Payout.Create(
            payoutCode: "PO-20260901-TESTSEED",
            operatorUserId: operatorId,
            months: [new PayoutMonthTotal(
                new DateOnly(2026, 9, 1), EligibleBookingCount: 1, ParticipantCount: 1,
                PaymentsReceived: 12_000_000m, DiscountsGiven: 0m, RefundedAmount: 0m,
                CommissionAmount: 1_200_000m, PayableNetAmount: 10_800_000m)],
            cutOffAtUtc: new DateTimeOffset(2026, 10, 3, 8, 15, 0, TimeSpan.Zero),
            adjustmentsApplied: 0m,
            minimumAmountChecked: 1_000_000m,
            bankName: "Vietcombank",
            accountHolderName: "Nguyen Van A",
            accountNumberMasked: "****1234",
            accountNumberProtected: "protected:1029384756",
            requestedAtUtc: new DateTimeOffset(2026, 10, 3, 8, 15, 0, TimeSpan.Zero));
        db.Payouts.Add(payout);
        await db.SaveChangesAsync();

        return new SeedResult(staffId, payout.Id);
    }

    private sealed record SeedResult(long StaffId, long PayoutId);

    private sealed class PayoutDecisionSaveBarrier(int participants) : SaveChangesInterceptor
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
            var isPayoutDecision = eventData.Context?.ChangeTracker
                .Entries<Payout>()
                .Any(entry => entry.State == EntityState.Modified &&
                    entry.Property(payout => payout.Status).IsModified) == true;

            if (!isPayoutDecision)
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