using System.Reflection;

using FluentAssertions;

using TripMate.Application.Features.Payouts.DailySettlement;
using TripMate.Application.UnitTests.TestUtilities;
using TripMate.Domain.Common;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Application.UnitTests.Features.Payouts;

// DAILY-PAYOUT-TRANSFER, Task 7/7A: the hourly retry sweep (PD-7, PD-7a, PD-17).
public sealed class DailyPayoutRetrySweepProcessorTests
{
    private static readonly DateTimeOffset RunTime = new(2026, 11, 9, 3, 0, 0, TimeSpan.Zero); // 10:00 VN

    private static async Task<PayoutBatch> SeedProcessingBatchAsync(TestDbContext db, DateTimeOffset lastAttemptAtUtc, int attemptCount = 1)
    {
        var batch = PayoutBatch.Create(DailyPayoutClock.VietnamDateOf(RunTime), tourOperatorUserId: 7, RunTime);
        var settlement = PayoutSettlement.Create(
            1, bookingId: 1, tourOperatorUserId: 7, runDate: DailyPayoutClock.VietnamDateOf(RunTime),
            kind: PayoutSettlementKind.Main, grossAmount: 2_000_000m, commissionRate: 10m,
            commissionAmount: 200_000m, netAmount: 1_800_000m, debtOffsetAmount: 0m,
            payoutAmount: 1_800_000m, createdAtUtc: RunTime);
        batch.AddSettlement(settlement);
        db.PayoutBatches.Add(batch);
        await db.SaveChangesAsync();

        batch.MarkProcessing(RunTime); // attempt 1
        for (var i = 1; i < attemptCount; i++)
            batch.RecordRetryAttempt(RunTime.AddHours(i));
        // Force the recorded last-attempt time to the caller's choice (simulating elapsed time).
        typeof(PayoutBatch).GetProperty(nameof(PayoutBatch.LastAttemptAtUtc), BindingFlags.Public | BindingFlags.Instance)!
            .SetValue(batch, lastAttemptAtUtc);
        await db.SaveChangesAsync();
        return batch;
    }

    private static DailyPayoutRetrySweepProcessor CreateSweeper(TestDbContext db, IPayoutTransferGateway gateway, DateTimeOffset nowUtc) =>
        new(db, gateway, new PayoutTestHelpers.FixedClock(nowUtc));

    [Fact]
    public async Task SweepAsync_BeforeOneHourElapsed_DoesNothing()
    {
        await using var db = TestDbContext.Create();
        var batch = await SeedProcessingBatchAsync(db, lastAttemptAtUtc: RunTime);
        var gateway = new SimulatedPayoutTransferGateway(new FixedOutcomeScenario(PayoutTransferOutcome.Succeeded));

        var acted = await CreateSweeper(db, gateway, RunTime.AddMinutes(30)).SweepAsync(CancellationToken.None);

        acted.Should().Be(0);
        batch.Status.Should().Be(PayoutSettlementStatus.Processing);
    }

    [Fact]
    public async Task SweepAsync_LookupShowsSucceeded_SettlesWithoutResending()
    {
        await using var db = TestDbContext.Create();
        var batch = await SeedProcessingBatchAsync(db, lastAttemptAtUtc: RunTime);
        var gateway = new SimulatedPayoutTransferGateway(new FixedOutcomeScenario(PayoutTransferOutcome.Succeeded));

        var acted = await CreateSweeper(db, gateway, RunTime.AddHours(1)).SweepAsync(CancellationToken.None);

        acted.Should().Be(1);
        batch.Status.Should().Be(PayoutSettlementStatus.Settled);
        batch.AttemptCount.Should().Be(1, "a confirmatory lookup is not counted as a transfer attempt");
    }

    // DAILY-PAYOUT-TRANSFER Task 7A (PD-17): the lookup revealing Succeeded for a batch TripMate
    // still thought Processing is itself the discrepancy — record it, audit it, alert Administrators.
    [Fact]
    public async Task SweepAsync_LookupShowsSucceeded_RecordsReconciliationItemAuditLogAndAdministratorAlert()
    {
        await using var db = TestDbContext.Create();
        var admin = new User { Id = 99, Email = "admin@test.local", FullName = "Admin", Role = UserRole.Administrator, Status = AccountStatus.Active };
        db.Users.Add(admin);
        await db.SaveChangesAsync();
        var batch = await SeedProcessingBatchAsync(db, lastAttemptAtUtc: RunTime);
        var gateway = new SimulatedPayoutTransferGateway(new FixedOutcomeScenario(PayoutTransferOutcome.Succeeded));
        var nowUtc = RunTime.AddHours(1);

        await CreateSweeper(db, gateway, nowUtc).SweepAsync(CancellationToken.None);

        var item = db.PayoutReconciliationItems.Should().ContainSingle(i => i.BatchId == batch.Id).Subject;
        item.ExpectedStatus.Should().Be(PayoutSettlementStatus.Processing);
        item.ObservedOutcome.Should().Be(PayoutReconciliationItem.ObservedOutcomeSucceeded);
        item.DetectedAtUtc.Should().Be(nowUtc);

        db.AuditLogs.Should().ContainSingle(a =>
            a.ActionType == AuditActionTypes.PayoutReconciliationDiscrepancyDetected && a.AffectedEntityId == batch.Id);
        db.Notifications.Should().ContainSingle(n => n.UserId == admin.Id);
    }

    [Fact]
    public async Task SweepAsync_StillFailingWithinBudget_Resends()
    {
        await using var db = TestDbContext.Create();
        var batch = await SeedProcessingBatchAsync(db, lastAttemptAtUtc: RunTime);
        // Lookup reports Timeout (unresolved); resend succeeds.
        var gateway = new SimulatedPayoutTransferGateway(new LookupThenSendScenario(PayoutTransferOutcome.Timeout, PayoutTransferOutcome.Succeeded));

        var acted = await CreateSweeper(db, gateway, RunTime.AddHours(1)).SweepAsync(CancellationToken.None);

        acted.Should().Be(1);
        batch.AttemptCount.Should().Be(2);
        batch.Status.Should().Be(PayoutSettlementStatus.Settled);
        db.PayoutSettlementAttempts.Should().ContainSingle(a => a.BatchId == batch.Id && a.AttemptNumber == 2);
        db.PayoutReconciliationItems.Should().BeEmpty("an unresolved lookup followed by a normal resend is not a discrepancy");
    }

    [Fact]
    public async Task SweepAsync_ExhaustsRetryBudget_MarksFailed()
    {
        await using var db = TestDbContext.Create();
        // Already at attempt 4 (1 initial + 3 retries) -> no more resends allowed.
        var batch = await SeedProcessingBatchAsync(db, lastAttemptAtUtc: RunTime.AddHours(3), attemptCount: 4);
        var gateway = new SimulatedPayoutTransferGateway(new FixedOutcomeScenario(PayoutTransferOutcome.Timeout));

        var acted = await CreateSweeper(db, gateway, RunTime.AddHours(4)).SweepAsync(CancellationToken.None);

        acted.Should().Be(1);
        batch.Status.Should().Be(PayoutSettlementStatus.SettlementFailed);
        batch.AttemptCount.Should().Be(4, "exhausting the budget must not count as another attempt");
        db.PayoutReconciliationItems.Should().BeEmpty("a Timeout lookup is unresolved, not a discrepancy with what TripMate believed");
    }

    [Fact]
    public async Task SweepAsync_PastSameDayCutoff_MarksFailed_EvenWithinAttemptBudget()
    {
        await using var db = TestDbContext.Create();
        var batch = await SeedProcessingBatchAsync(db, lastAttemptAtUtc: RunTime.AddHours(1), attemptCount: 1);
        var gateway = new SimulatedPayoutTransferGateway(new FixedOutcomeScenario(PayoutTransferOutcome.Timeout));

        // 13:30 VN (06:30 UTC) on the run's own day -> past the 13:00 cutoff.
        var pastCutoff = new DateTimeOffset(2026, 11, 9, 6, 30, 0, TimeSpan.Zero);
        var acted = await CreateSweeper(db, gateway, pastCutoff).SweepAsync(CancellationToken.None);

        acted.Should().Be(1);
        batch.Status.Should().Be(PayoutSettlementStatus.SettlementFailed);
    }

    [Fact]
    public async Task SweepAsync_LookupShowsRejected_MarksFailedImmediately()
    {
        await using var db = TestDbContext.Create();
        var batch = await SeedProcessingBatchAsync(db, lastAttemptAtUtc: RunTime);
        var gateway = new SimulatedPayoutTransferGateway(new FixedOutcomeScenario(PayoutTransferOutcome.Rejected));

        await CreateSweeper(db, gateway, RunTime.AddHours(1)).SweepAsync(CancellationToken.None);

        batch.Status.Should().Be(PayoutSettlementStatus.SettlementFailed);
        batch.AttemptCount.Should().Be(1, "a permanent rejection from the lookup stops retries without another send");
    }

    [Fact]
    public async Task SweepAsync_LookupShowsRejected_RecordsReconciliationItemAuditLogAndAdministratorAlert()
    {
        await using var db = TestDbContext.Create();
        var admin = new User { Id = 99, Email = "admin@test.local", FullName = "Admin", Role = UserRole.Administrator, Status = AccountStatus.Active };
        db.Users.Add(admin);
        await db.SaveChangesAsync();
        var batch = await SeedProcessingBatchAsync(db, lastAttemptAtUtc: RunTime);
        var gateway = new SimulatedPayoutTransferGateway(new FixedOutcomeScenario(PayoutTransferOutcome.Rejected));
        var nowUtc = RunTime.AddHours(1);

        await CreateSweeper(db, gateway, nowUtc).SweepAsync(CancellationToken.None);

        var item = db.PayoutReconciliationItems.Should().ContainSingle(i => i.BatchId == batch.Id).Subject;
        item.ExpectedStatus.Should().Be(PayoutSettlementStatus.Processing);
        item.ObservedOutcome.Should().Be(PayoutReconciliationItem.ObservedOutcomeRejected);
        item.DetectedAtUtc.Should().Be(nowUtc);

        db.AuditLogs.Should().ContainSingle(a =>
            a.ActionType == AuditActionTypes.PayoutReconciliationDiscrepancyDetected && a.AffectedEntityId == batch.Id);
        db.Notifications.Should().ContainSingle(n => n.UserId == admin.Id);
    }

    /// <summary>
    /// The other exhaustion test above seeds straight to attempt 4 and never drives the sweep
    /// through the real hourly sequence a scheduler would (11:00, 12:00, 13:00, 14:00 VN). Doing
    /// that here is what the plan's "3 retries, last one at 13:00" actually describes: the 13:00
    /// sweep must still be allowed to send the 3rd retry (attempt 4) — only the 14:00 sweep, once
    /// the budget of 3 retries is exhausted, fails the batch.
    /// </summary>
    [Fact]
    public async Task SweepAsync_NaturalHourlySequenceFromTenAm_SendsAllThreeRetriesThenFailsOnBudget()
    {
        await using var db = TestDbContext.Create();
        var batch = await SeedProcessingBatchAsync(db, lastAttemptAtUtc: RunTime); // attempt 1 @ 10:00 VN
        var gateway = new SimulatedPayoutTransferGateway(new FixedOutcomeScenario(PayoutTransferOutcome.Timeout));

        await CreateSweeper(db, gateway, RunTime.AddHours(1)).SweepAsync(CancellationToken.None); // 11:00 VN -> retry 2
        await CreateSweeper(db, gateway, RunTime.AddHours(2)).SweepAsync(CancellationToken.None); // 12:00 VN -> retry 3
        await CreateSweeper(db, gateway, RunTime.AddHours(3)).SweepAsync(CancellationToken.None); // 13:00 VN -> retry 4 (last one)

        batch.Status.Should().Be(PayoutSettlementStatus.Processing, "the 13:00 retry is exactly on the cutoff, not past it");
        batch.AttemptCount.Should().Be(4);

        await CreateSweeper(db, gateway, RunTime.AddHours(4)).SweepAsync(CancellationToken.None); // 14:00 VN -> budget exhausted

        batch.Status.Should().Be(PayoutSettlementStatus.SettlementFailed);
        batch.AttemptCount.Should().Be(4, "exhausting the budget must not count as another attempt");
        // This test's seed helper sets attempt 1 directly (MarkProcessing) without writing its
        // PayoutSettlementAttempt row — that row is DailyPayoutRunProcessor's job in production.
        // The 11:00, 12:00 and 13:00 retries each write one real row (attempts 2, 3 and 4).
        db.PayoutSettlementAttempts.Count(a => a.BatchId == batch.Id).Should().Be(3);
    }
}