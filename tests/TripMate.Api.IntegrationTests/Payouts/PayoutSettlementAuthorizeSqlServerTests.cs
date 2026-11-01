using System.Net;

using FluentAssertions;

using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

using TripMate.Api.IntegrationTests.Infrastructure;
using TripMate.Domain.Common;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Api.IntegrationTests.Payouts;

/// <summary>
/// UC-65 against real SQL Server: InMemory cannot prove the Status concurrency-token predicate,
/// the unique work item per payout, or the CK_Payouts_LifecycleState rule for the new outcomes.
/// </summary>
[Collection(nameof(TripMateApiFactory))]
public sealed class PayoutSettlementAuthorizeSqlServerTests
{
    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task ConcurrentAuthorizations_OneSucceedsOneConflicts_AndQueueExactlyOneWorkItem()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        var seed = await SeedApprovedPayoutAsync(database);
        var barrier = new AuthorizationSaveBarrier(2);
        await using var factory = new TripMateApiFactory(
            sqlServerConnectionString: database.ConnectionString,
            saveChangesInterceptor: barrier);
        using var first = factory.CreateAuthenticatedClient(seed.AdministratorId, UserRole.Administrator);
        using var second = factory.CreateAuthenticatedClient(seed.AdministratorId, UserRole.Administrator);
        var route = $"/api/v1/admin/payout-settlements/{seed.PayoutId}/authorize";

        var responses = await Task.WhenAll(
            first.PostAsync(route, content: null),
            second.PostAsync(route, content: null));

        responses.Select(response => response.StatusCode).Should().BeEquivalentTo(
            [HttpStatusCode.Accepted, HttpStatusCode.Conflict]);
        barrier.Arrivals.Should().Be(2, "both requests must load ApprovedForSettlement before either save proceeds");

        await using var verification = database.CreateDbContext();
        var payout = await verification.Payouts.AsNoTracking().SingleAsync(item => item.Id == seed.PayoutId);
        payout.Status.Should().Be(Payout.StatusSettlementProcessing);
        payout.BankBin.Should().Be("970436");
        payout.SettlementAuthorizedBy.Should().Be(seed.AdministratorId);
        (await verification.PayoutSettlementWorkItems.AsNoTracking()
            .CountAsync(item => item.PayoutId == seed.PayoutId)).Should().Be(1);
        (await verification.AuditLogs.AsNoTracking().CountAsync(item =>
            item.AffectedEntityId == seed.PayoutId
            && item.ActionType == AuditActionTypes.PayoutSettlementAuthorized)).Should().Be(1,
            "the losing request's audit insert is rolled back with its state change");
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task LifecycleConstraint_AcceptsSettledAndFailedOutcomesProducedByTheDomainMethods()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        var settled = await SeedApprovedPayoutAsync(database, suffix: "a");
        var failed = await SeedApprovedPayoutAsync(database, suffix: "b");
        var now = new DateTimeOffset(2026, 11, 1, 9, 0, 0, TimeSpan.Zero);

        await using (var db = database.CreateDbContext())
        {
            var toSettle = await db.Payouts.SingleAsync(item => item.Id == settled.PayoutId);
            toSettle.AuthorizeSettlement(settled.AdministratorId, now, "970436");
            toSettle.RecordGatewayAttempt(now.AddMinutes(1));
            toSettle.SetGatewayReference("payos-123");
            toSettle.RecordGatewayAttempt(now.AddMinutes(30));
            toSettle.MarkSettled();

            var toFail = await db.Payouts.SingleAsync(item => item.Id == failed.PayoutId);
            toFail.AuthorizeSettlement(failed.AdministratorId, now, "970436");
            toFail.RecordGatewayAttempt(now.AddMinutes(1));
            toFail.MarkSettlementFailed("gateway_rejected");

            await db.SaveChangesAsync();
        }

        await using var verification = database.CreateDbContext();
        (await verification.Payouts.AsNoTracking().SingleAsync(item => item.Id == settled.PayoutId))
            .Status.Should().Be(Payout.StatusSettled);
        (await verification.Payouts.AsNoTracking().SingleAsync(item => item.Id == failed.PayoutId))
            .Status.Should().Be(Payout.StatusSettlementFailed);
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task LifecycleConstraint_RejectsAnOutcomeWithoutAnyRecordedGatewayAttempt()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        var seed = await SeedApprovedPayoutAsync(database);
        await database.ExecuteNonQueryAsync($"""
            UPDATE payment.Payouts
            SET status = 'SettlementProcessing', settlement_authorized_by = {seed.AdministratorId},
                settlement_authorized_at = SYSUTCDATETIME(), bank_bin = '970436'
            WHERE payout_id = {seed.PayoutId};
            """);

        Func<Task> settleWithoutAttempt = () => database.ExecuteNonQueryAsync($"""
            UPDATE payment.Payouts SET status = 'Settled', gateway_reference = 'payos-123'
            WHERE payout_id = {seed.PayoutId};
            """);
        Func<Task> failWithoutAttempt = () => database.ExecuteNonQueryAsync($"""
            UPDATE payment.Payouts SET status = 'SettlementFailed', gateway_failure_reason = 'gateway_rejected'
            WHERE payout_id = {seed.PayoutId};
            """);

        await settleWithoutAttempt.Should().ThrowAsync<SqlException>();
        await failWithoutAttempt.Should().ThrowAsync<SqlException>();
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task WorkItem_AllowsOnlyOnePerPayoutEvenWhenInsertedDirectly()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        var seed = await SeedApprovedPayoutAsync(database);
        var now = DateTimeOffset.UtcNow;

        await using var db = database.CreateDbContext();
        db.PayoutSettlementWorkItems.Add(PayoutSettlementWorkItem.Create(seed.PayoutId, now));
        await db.SaveChangesAsync();

        await using var other = database.CreateDbContext();
        other.PayoutSettlementWorkItems.Add(PayoutSettlementWorkItem.Create(seed.PayoutId, now));
        Func<Task> duplicate = () => other.SaveChangesAsync();

        var thrown = await duplicate.Should().ThrowAsync<DbUpdateException>();
        new TripMate.Infrastructure.Persistence.SqlServerPayoutPersistenceErrorClassifier()
            .IsSettlementWorkItemConflict(thrown.Which).Should().BeTrue();
    }

    private static async Task<SeedResult> SeedApprovedPayoutAsync(SqlServerTestDatabase database, string suffix = "")
    {
        await database.ExecuteNonQueryAsync($"""
            DECLARE @operator BIGINT;
            INSERT dbo.Users(role, email, full_name, status)
            VALUES ('TourOperator', N'settlement-operator{suffix}@example.com', N'Settlement Tours {suffix}', 'Active');
            SET @operator = SCOPE_IDENTITY();
            INSERT dbo.OperatorProfiles(user_id, company_name, tax_code, business_license_no)
            VALUES (@operator, N'Settlement Tours {suffix}', N'TAX-SET-{suffix}', N'LIC-SET-{suffix}');

            INSERT dbo.Users(role, email, full_name, status)
            VALUES ('Administrator', N'settlement-admin{suffix}@example.com', N'Settlement Admin {suffix}', 'Active');
            """);

        var operatorId = await database.ExecuteScalarAsync<long>(
            $"SELECT user_id FROM dbo.Users WHERE email = N'settlement-operator{suffix}@example.com'");
        var administratorId = await database.ExecuteScalarAsync<long>(
            $"SELECT user_id FROM dbo.Users WHERE email = N'settlement-admin{suffix}@example.com'");

        await using var db = database.CreateDbContext();
        var payout = Payout.Create(
            payoutCode: $"TMP-SET{suffix}-{Guid.NewGuid():N}"[..Payout.PayoutCodeMaxLength],
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
        payout.Approve(administratorId, new DateTimeOffset(2026, 10, 4, 8, 15, 0, TimeSpan.Zero));
        db.Payouts.Add(payout);
        await db.SaveChangesAsync();

        return new SeedResult(administratorId, payout.Id);
    }

    private sealed record SeedResult(long AdministratorId, long PayoutId);

    private sealed class AuthorizationSaveBarrier(int participants) : SaveChangesInterceptor
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
            var isAuthorization = eventData.Context?.ChangeTracker
                .Entries<Payout>()
                .Any(entry => entry.State == EntityState.Modified &&
                    entry.Property(payout => payout.Status).IsModified &&
                    entry.Entity.Status == Payout.StatusSettlementProcessing) == true;

            if (!isAuthorization)
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
