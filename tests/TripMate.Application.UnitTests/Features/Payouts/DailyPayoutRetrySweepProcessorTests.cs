using System.Reflection;

using FluentAssertions;

using Microsoft.EntityFrameworkCore;

using TripMate.Application.Features.Payouts.DailySettlement;
using TripMate.Application.UnitTests.TestUtilities;
using TripMate.Domain.Common;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Application.UnitTests.Features.Payouts;

// DAILY-PAYOUT-TRANSFER, spec v7 (SRS rev10-6, 3.10.5 UC-74; PD-7, PD-7a, PD-17, BR-145, BR-160,
// BR-172): the sweep that attempts every Scheduled batch that is due. A batch rests as Scheduled
// between attempts (not Processing) — no same-day cutoff: only the 3-retry budget bounds it.
public sealed class DailyPayoutRetrySweepProcessorTests
{
    private static readonly DateTimeOffset RunTime = new(2026, 11, 9, 3, 0, 0, TimeSpan.Zero); // 10:00 VN

    private static async Task SeedOperatorWithBankAccountAsync(TestDbContext db, long operatorId)
    {
        db.Users.Add(new User
        {
            Id = operatorId, Email = $"operator{operatorId}@test.local", FullName = "Operator",
            Role = UserRole.TourOperator, Status = AccountStatus.Active,
        });
        db.OperatorProfiles.Add(new OperatorProfile
        {
            UserId = operatorId, CompanyName = "Company", TaxCode = $"TAX-{operatorId}", BusinessLicenseNo = $"LIC-{operatorId}",
            ApprovalStatus = OperatorApprovalStatus.Approved, CommissionRate = 10m,
            BankName = "Test Bank", AccountHolderName = "Holder", AccountNumberMasked = "****1234", AccountNumberProtected = "protected",
        });
        await db.SaveChangesAsync();
    }

    /// <summary>Seeds a batch resting as Scheduled with <paramref name="attemptCount"/> attempts
    /// already made (0 = never attempted) and its last attempt at <paramref name="lastAttemptAtUtc"/>
    /// — the resting state between hourly retries (SRS 3.10.5, abnormal 3.a1).</summary>
    private static async Task<PayoutBatch> SeedScheduledBatchAsync(
        TestDbContext db, DateTimeOffset? lastAttemptAtUtc, int attemptCount = 1, long operatorId = 7)
    {
        var batch = PayoutBatch.Create(DailyPayoutClock.VietnamDateOf(RunTime), tourOperatorUserId: operatorId, RunTime);
        var settlement = PayoutSettlement.Create(
            1, bookingId: 1, tourOperatorUserId: operatorId, runDate: DailyPayoutClock.VietnamDateOf(RunTime),
            kind: PayoutSettlementKind.Advance, grossAmount: 1_400_000m, commissionRate: 10m,
            commissionAmount: 0m, netAmount: 1_400_000m, debtOffsetAmount: 0m,
            payoutAmount: 1_400_000m, createdAtUtc: RunTime);
        batch.AddSettlement(settlement);
        db.PayoutBatches.Add(batch);
        await db.SaveChangesAsync();

        typeof(PayoutBatch).GetProperty(nameof(PayoutBatch.AttemptCount), BindingFlags.Public | BindingFlags.Instance)!
            .SetValue(batch, attemptCount);
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
        await SeedOperatorWithBankAccountAsync(db, 7);
        var batch = await SeedScheduledBatchAsync(db, lastAttemptAtUtc: RunTime);
        var gateway = new SimulatedPayoutTransferGateway(new FixedOutcomeScenario(PayoutTransferOutcome.Succeeded));

        var acted = await CreateSweeper(db, gateway, RunTime.AddMinutes(30)).SweepAsync(CancellationToken.None);

        acted.Should().Be(0);
        batch.Status.Should().Be(PayoutSettlementStatus.Scheduled);
    }

    [Fact]
    public async Task SweepAsync_NeverAttempted_IsAlwaysDue_AndSendsImmediately()
    {
        await using var db = TestDbContext.Create();
        await SeedOperatorWithBankAccountAsync(db, 7);
        var batch = await SeedScheduledBatchAsync(db, lastAttemptAtUtc: null, attemptCount: 0);
        var gateway = new SimulatedPayoutTransferGateway(new FixedOutcomeScenario(PayoutTransferOutcome.Succeeded));

        var acted = await CreateSweeper(db, gateway, RunTime).SweepAsync(CancellationToken.None);

        acted.Should().Be(1);
        batch.Status.Should().Be(PayoutSettlementStatus.Settled);
        batch.AttemptCount.Should().Be(1);
    }

    [Fact]
    public async Task SweepAsync_OperatorLocked_LeavesBatchScheduledUntouched()
    {
        // SRS 3.10.5, 1.a1 (BR-160): no attempt is made at all while the operator is Locked.
        await using var db = TestDbContext.Create();
        await SeedOperatorWithBankAccountAsync(db, 7);
        var operatorUser = await db.Users.SingleAsync(u => u.Id == 7);
        operatorUser.Status = AccountStatus.Locked;
        await db.SaveChangesAsync();
        var batch = await SeedScheduledBatchAsync(db, lastAttemptAtUtc: null, attemptCount: 0);
        var gateway = new SimulatedPayoutTransferGateway(new FixedOutcomeScenario(PayoutTransferOutcome.Succeeded));

        var acted = await CreateSweeper(db, gateway, RunTime).SweepAsync(CancellationToken.None);

        acted.Should().Be(0);
        batch.Status.Should().Be(PayoutSettlementStatus.Scheduled);
        batch.AttemptCount.Should().Be(0);
    }

    [Fact]
    public async Task SweepAsync_OperatorWithoutBankAccount_FailsWithoutConsumingAnAttempt()
    {
        // SRS 3.10.5, 1.b1: missing/incomplete bank account -> SettlementFailed, no gateway call.
        await using var db = TestDbContext.Create();
        db.Users.Add(new User { Id = 7, Email = "op7@test.local", FullName = "Operator", Role = UserRole.TourOperator, Status = AccountStatus.Active });
        db.OperatorProfiles.Add(new OperatorProfile
        {
            UserId = 7, CompanyName = "Company", TaxCode = "TAX-7", BusinessLicenseNo = "LIC-7",
            ApprovalStatus = OperatorApprovalStatus.Approved, CommissionRate = 10m,
        });
        await db.SaveChangesAsync();
        var batch = await SeedScheduledBatchAsync(db, lastAttemptAtUtc: null, attemptCount: 0);
        var gateway = new SimulatedPayoutTransferGateway(new FixedOutcomeScenario(PayoutTransferOutcome.Succeeded));

        var acted = await CreateSweeper(db, gateway, RunTime).SweepAsync(CancellationToken.None);

        acted.Should().Be(1);
        batch.Status.Should().Be(PayoutSettlementStatus.SettlementFailed);
        batch.AttemptCount.Should().Be(0, "no gateway attempt was ever made");
        db.Notifications.Should().ContainSingle(n => n.UserId == 7); // MSG146
    }

    [Fact]
    public async Task SweepAsync_LookupShowsSucceeded_SettlesWithoutResending_AndConsumesNoNewAttempt()
    {
        await using var db = TestDbContext.Create();
        await SeedOperatorWithBankAccountAsync(db, 7);
        var batch = await SeedScheduledBatchAsync(db, lastAttemptAtUtc: RunTime);
        var gateway = new SimulatedPayoutTransferGateway(new FixedOutcomeScenario(PayoutTransferOutcome.Succeeded));

        var acted = await CreateSweeper(db, gateway, RunTime.AddHours(1)).SweepAsync(CancellationToken.None);

        acted.Should().Be(1);
        batch.Status.Should().Be(PayoutSettlementStatus.Settled);
        batch.AttemptCount.Should().Be(1, "a confirmatory lookup is not counted as a transfer attempt");
    }

    [Fact]
    public async Task SweepAsync_LookupShowsSucceeded_RecordsReconciliationItemAuditLogAndAdministratorAlert()
    {
        await using var db = TestDbContext.Create();
        await SeedOperatorWithBankAccountAsync(db, 7);
        var admin = new User { Id = 99, Email = "admin@test.local", FullName = "Admin", Role = UserRole.Administrator, Status = AccountStatus.Active };
        db.Users.Add(admin);
        await db.SaveChangesAsync();
        var batch = await SeedScheduledBatchAsync(db, lastAttemptAtUtc: RunTime);
        var gateway = new SimulatedPayoutTransferGateway(new FixedOutcomeScenario(PayoutTransferOutcome.Succeeded));
        var nowUtc = RunTime.AddHours(1);

        await CreateSweeper(db, gateway, nowUtc).SweepAsync(CancellationToken.None);

        var item = db.PayoutReconciliationItems.Should().ContainSingle(i => i.BatchId == batch.Id).Subject;
        item.ExpectedStatus.Should().Be(PayoutSettlementStatus.Scheduled);
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
        await SeedOperatorWithBankAccountAsync(db, 7);
        var batch = await SeedScheduledBatchAsync(db, lastAttemptAtUtc: RunTime);
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
    public async Task SweepAsync_FailedAttemptWithinBudget_ReturnsToScheduled_AndNotifiesAdministrators()
    {
        // SRS 3.10.5, abnormal 3.a1: rests as Scheduled (not Processing), MSG175 to Administrator.
        await using var db = TestDbContext.Create();
        await SeedOperatorWithBankAccountAsync(db, 7);
        var admin = new User { Id = 99, Email = "admin@test.local", FullName = "Admin", Role = UserRole.Administrator, Status = AccountStatus.Active };
        db.Users.Add(admin);
        await db.SaveChangesAsync();
        var batch = await SeedScheduledBatchAsync(db, lastAttemptAtUtc: null, attemptCount: 0);
        var gateway = new SimulatedPayoutTransferGateway(new FixedOutcomeScenario(PayoutTransferOutcome.Timeout));

        var acted = await CreateSweeper(db, gateway, RunTime).SweepAsync(CancellationToken.None);

        acted.Should().Be(1);
        batch.Status.Should().Be(PayoutSettlementStatus.Scheduled);
        batch.AttemptCount.Should().Be(1);
        db.Notifications.Should().ContainSingle(n => n.UserId == admin.Id); // MSG175
    }

    [Fact]
    public async Task SweepAsync_LastAllowedAttemptFails_MarksFailedImmediately_AndNotifiesAdministrators()
    {
        // BR-145/SRS 3.10.5, 3.b1: the 4th attempt (1 initial + 3 retries) failing resolves to
        // SettlementFailed within the same call — no further waiting cycle is needed.
        await using var db = TestDbContext.Create();
        await SeedOperatorWithBankAccountAsync(db, 7);
        var admin = new User { Id = 99, Email = "admin@test.local", FullName = "Admin", Role = UserRole.Administrator, Status = AccountStatus.Active };
        db.Users.Add(admin);
        await db.SaveChangesAsync();
        var batch = await SeedScheduledBatchAsync(db, lastAttemptAtUtc: RunTime.AddHours(2), attemptCount: 3);
        var gateway = new SimulatedPayoutTransferGateway(new FixedOutcomeScenario(PayoutTransferOutcome.Timeout));

        var acted = await CreateSweeper(db, gateway, RunTime.AddHours(3)).SweepAsync(CancellationToken.None);

        acted.Should().Be(1);
        batch.Status.Should().Be(PayoutSettlementStatus.SettlementFailed);
        batch.AttemptCount.Should().Be(4);
        db.Notifications.Should().ContainSingle(n => n.UserId == admin.Id); // MSG176
    }

    [Fact]
    public async Task SweepAsync_LookupShowsRejected_MarksFailedImmediately()
    {
        await using var db = TestDbContext.Create();
        await SeedOperatorWithBankAccountAsync(db, 7);
        var batch = await SeedScheduledBatchAsync(db, lastAttemptAtUtc: RunTime);
        var gateway = new SimulatedPayoutTransferGateway(new FixedOutcomeScenario(PayoutTransferOutcome.Rejected));

        await CreateSweeper(db, gateway, RunTime.AddHours(1)).SweepAsync(CancellationToken.None);

        batch.Status.Should().Be(PayoutSettlementStatus.SettlementFailed);
        batch.AttemptCount.Should().Be(1, "a permanent rejection from the lookup stops retries without another send");
    }

    [Fact]
    public async Task SweepAsync_LookupShowsRejected_RecordsReconciliationItemAuditLogAndAdministratorAlert()
    {
        await using var db = TestDbContext.Create();
        await SeedOperatorWithBankAccountAsync(db, 7);
        var admin = new User { Id = 99, Email = "admin@test.local", FullName = "Admin", Role = UserRole.Administrator, Status = AccountStatus.Active };
        db.Users.Add(admin);
        await db.SaveChangesAsync();
        var batch = await SeedScheduledBatchAsync(db, lastAttemptAtUtc: RunTime);
        var gateway = new SimulatedPayoutTransferGateway(new FixedOutcomeScenario(PayoutTransferOutcome.Rejected));
        var nowUtc = RunTime.AddHours(1);

        await CreateSweeper(db, gateway, nowUtc).SweepAsync(CancellationToken.None);

        var item = db.PayoutReconciliationItems.Should().ContainSingle(i => i.BatchId == batch.Id).Subject;
        item.ExpectedStatus.Should().Be(PayoutSettlementStatus.Scheduled);
        item.ObservedOutcome.Should().Be(PayoutReconciliationItem.ObservedOutcomeRejected);
        item.DetectedAtUtc.Should().Be(nowUtc);

        db.AuditLogs.Should().ContainSingle(a =>
            a.ActionType == AuditActionTypes.PayoutReconciliationDiscrepancyDetected && a.AffectedEntityId == batch.Id);
        // Two alerts for the same admin here: one from the reconciliation discrepancy, one from
        // the resulting SettlementFailed transition (BR-172) — both are real, independent events.
        db.Notifications.Should().HaveCountGreaterThanOrEqualTo(2).And.Contain(n => n.UserId == admin.Id);
    }

    /// <summary>
    /// Drives the sweep through the real hourly sequence a scheduler would (10:00, 11:00, 12:00,
    /// 13:00 VN) rather than seeding straight to the last attempt. No same-day cutoff in v7: the
    /// budget of 3 retries (BR-145) is the only bound, and the 4th (last allowed) attempt that
    /// fails resolves to SettlementFailed immediately, within that same hourly sweep.
    /// </summary>
    [Fact]
    public async Task SweepAsync_NaturalHourlySequenceFromTenAm_SendsAllFourAttemptsThenFailsOnTheLastOne()
    {
        await using var db = TestDbContext.Create();
        await SeedOperatorWithBankAccountAsync(db, 7);
        var batch = await SeedScheduledBatchAsync(db, lastAttemptAtUtc: RunTime, attemptCount: 1); // attempt 1 @ 10:00 VN
        var gateway = new SimulatedPayoutTransferGateway(new FixedOutcomeScenario(PayoutTransferOutcome.Timeout));

        await CreateSweeper(db, gateway, RunTime.AddHours(1)).SweepAsync(CancellationToken.None); // 11:00 VN -> attempt 2
        batch.Status.Should().Be(PayoutSettlementStatus.Scheduled);
        batch.AttemptCount.Should().Be(2);

        await CreateSweeper(db, gateway, RunTime.AddHours(2)).SweepAsync(CancellationToken.None); // 12:00 VN -> attempt 3
        batch.Status.Should().Be(PayoutSettlementStatus.Scheduled);
        batch.AttemptCount.Should().Be(3);

        await CreateSweeper(db, gateway, RunTime.AddHours(3)).SweepAsync(CancellationToken.None); // 13:00 VN -> attempt 4 (last allowed), fails

        batch.Status.Should().Be(PayoutSettlementStatus.SettlementFailed);
        batch.AttemptCount.Should().Be(4, "1 initial + 3 retries, BR-145");
        // Attempt 1 was seeded directly (no PayoutSettlementAttempt row, matching production
        // where DailyPayoutRunProcessor writes that row — not this test's seed helper). The
        // 11:00, 12:00 and 13:00 sweeps each write one real row (attempts 2, 3 and 4).
        db.PayoutSettlementAttempts.Count(a => a.BatchId == batch.Id).Should().Be(3);
    }
}
