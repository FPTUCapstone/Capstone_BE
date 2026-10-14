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
/// simulated <see cref="DbUpdateConcurrencyException"/>; this proves the real race.
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

            DECLARE @payout BIGINT;
            INSERT payment.Payouts(operator_user_id, period_start, period_end,
                gross_revenue, commission_amount, net_amount, status, requested_at,
                reviewed_by, reviewed_at, rejection_reason, settlement_authorized_by,
                settlement_authorized_at, gateway_reference, gateway_failure_reason,
                gateway_attempt_count, last_gateway_attempt_at)
            VALUES (@operator, '2026-09-01', '2026-09-30', 12000000, 1200000, 10800000,
                    'PendingReview', '2026-10-03T08:15:00', NULL, NULL, NULL, NULL, NULL, NULL,
                    NULL, 0, NULL);
            """);

        var ids = await database.ExecuteScalarAsync<long>(
            "SELECT TOP 1 payout_id FROM payment.Payouts WHERE operator_user_id = " +
            "(SELECT user_id FROM dbo.Users WHERE email = N'concurrency-operator@example.com')");
        var staffId = await database.ExecuteScalarAsync<long>(
            "SELECT user_id FROM dbo.Users WHERE email = N'concurrency-staff@example.com'");

        return new SeedResult(staffId, ids);
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