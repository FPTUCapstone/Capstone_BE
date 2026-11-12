using FluentAssertions;

using TripMate.Application.Features.Payouts.DailySettlement;
using TripMate.Application.UnitTests.TestUtilities;
using TripMate.Domain.Common;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Application.UnitTests.Features.Payouts;

// DAILY-PAYOUT-TRANSFER, Task 4/5 (spec v6, PD-1, PD-1A, PD-2, PD-2A, PD-5, PD-11, PD-14).
public sealed class DailyPayoutRunProcessorTests
{
    // 10:30 Vietnam time on 2026-11-09 -> 03:30 UTC.
    private static readonly DateTimeOffset AtRunTime = new(2026, 11, 9, 3, 30, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset BeforeRunTime = new(2026, 11, 9, 1, 0, 0, TimeSpan.Zero); // 08:00 VN

    private static DailyPayoutRunProcessor CreateProcessor(
        TestDbContext db, IPayoutTransferGateway gateway, DateTimeOffset nowUtc)
    {
        var clock = new PayoutTestHelpers.FixedClock(nowUtc);
        var retryProcessor = new DailyPayoutRetrySweepProcessor(db, gateway, clock, new DailyPayoutEligibilityReader(db));
        return new(db, new DailyPayoutEligibilityReader(db), retryProcessor, clock);
    }

    [Fact]
    public async Task RunIfDueAsync_BeforeRunStart_ReturnsNotDueYet_AndCreatesNoRun()
    {
        await using var db = TestDbContext.Create();
        var processor = CreateProcessor(db, new SimulatedPayoutTransferGateway(new FixedOutcomeScenario(PayoutTransferOutcome.Succeeded)), BeforeRunTime);

        var outcome = await processor.RunIfDueAsync(CancellationToken.None);

        outcome.Should().Be(DailyPayoutRunOutcome.NotDueYet);
        db.PayoutRuns.Should().BeEmpty();
    }

    [Fact]
    public async Task RunIfDueAsync_BookingPastWindowWithTourNotYetEnded_CreatesOnlyAdvanceSettlement()
    {
        await using var db = TestDbContext.Create();
        var operatorUser = await DailyPayoutTestFixture.SeedOperatorAsync(db, 7);
        await DailyPayoutTestFixture.SeedTourScheduleAsync(db, tourId: 1, scheduleId: 1, operatorId: 7, endAtUtc: AtRunTime.AddDays(10));
        await DailyPayoutTestFixture.SeedConfirmedBookingAsync(db, bookingId: 1, scheduleId: 1, totalAmount: 2_000_000m, succeededAtUtc: AtRunTime.AddHours(-25));

        var processor = CreateProcessor(db, new SimulatedPayoutTransferGateway(new FixedOutcomeScenario(PayoutTransferOutcome.Succeeded)), AtRunTime);
        var outcome = await processor.RunIfDueAsync(CancellationToken.None);

        outcome.Should().Be(DailyPayoutRunOutcome.Ran);
        db.PayoutRuns.Should().ContainSingle(r => r.Status == PayoutRun.StatusCompleted);

        var batch = db.PayoutBatches.Should().ContainSingle().Subject;
        batch.TourOperatorUserId.Should().Be(7);
        batch.Status.Should().Be(PayoutSettlementStatus.Settled);
        batch.TotalAmount.Should().Be(1_400_000m, "Advance is 70% of PaidAmount");

        var settlement = db.PayoutSettlements.Should().ContainSingle().Subject;
        settlement.BookingId.Should().Be(1);
        settlement.Kind.Should().Be(PayoutSettlementKind.Advance);
        settlement.PayoutAmount.Should().Be(1_400_000m);
        settlement.Status.Should().Be(PayoutSettlementStatus.Settled);
    }

    [Fact]
    public async Task RunIfDueAsync_BookingPastWindowWithTourAlreadyEnded_CreatesBothAdvanceAndFinalSettlements()
    {
        await using var db = TestDbContext.Create();
        await DailyPayoutTestFixture.SeedOperatorAsync(db, 7);
        await DailyPayoutTestFixture.SeedTourScheduleAsync(db, 1, 1, 7, endAtUtc: AtRunTime.AddDays(-1));
        await DailyPayoutTestFixture.SeedConfirmedBookingAsync(db, 1, 1, totalAmount: 2_000_000m, succeededAtUtc: AtRunTime.AddHours(-25));

        var processor = CreateProcessor(db, new SimulatedPayoutTransferGateway(new FixedOutcomeScenario(PayoutTransferOutcome.Succeeded)), AtRunTime);
        await processor.RunIfDueAsync(CancellationToken.None);

        var batch = db.PayoutBatches.Should().ContainSingle().Subject;
        batch.TotalAmount.Should().Be(1_800_000m, "Advance (70%) + Final (20%) = 1,400,000 + 400,000");

        db.PayoutSettlements.Should().HaveCount(2);
        db.PayoutSettlements.Should().ContainSingle(s => s.Kind == PayoutSettlementKind.Advance && s.PayoutAmount == 1_400_000m);
        db.PayoutSettlements.Should().ContainSingle(s => s.Kind == PayoutSettlementKind.Final && s.PayoutAmount == 400_000m);
    }

    [Fact]
    public async Task RunIfDueAsync_BookingHasAProcessedPartialRefund_FinalExcludesTheRefundedAmount()
    {
        // BR-142: the Final (20%) share is computed on (PaidAmount - processed refunds) for that
        // booking. Advance (already settled by the time of this run) is never reduced by a later
        // refund — only Final, which has not been created yet, reflects it.
        await using var db = TestDbContext.Create();
        await DailyPayoutTestFixture.SeedOperatorAsync(db, 7);
        await DailyPayoutTestFixture.SeedTourScheduleAsync(db, 1, 1, 7, endAtUtc: AtRunTime.AddDays(-1));
        await DailyPayoutTestFixture.SeedConfirmedBookingAsync(db, 1, 1, totalAmount: 2_000_000m, succeededAtUtc: AtRunTime.AddHours(-25));
        await DailyPayoutTestFixture.SeedProcessedRefundAsync(db, bookingId: 1, amount: 500_000m, processedAtUtc: AtRunTime.AddHours(-10));

        var processor = CreateProcessor(db, new SimulatedPayoutTransferGateway(new FixedOutcomeScenario(PayoutTransferOutcome.Succeeded)), AtRunTime);
        await processor.RunIfDueAsync(CancellationToken.None);

        db.PayoutSettlements.Should().ContainSingle(s => s.Kind == PayoutSettlementKind.Advance && s.PayoutAmount == 1_400_000m,
            "Advance is 70% of the full PaidAmount, unaffected by a later refund");
        db.PayoutSettlements.Should().ContainSingle(s => s.Kind == PayoutSettlementKind.Final && s.PayoutAmount == 300_000m,
            "Final is 20% of (2,000,000 - 500,000 refunded) = 300,000");
    }

    [Fact]
    public async Task RunIfDueAsync_CalledTwiceSameDay_IsIdempotent()
    {
        await using var db = TestDbContext.Create();
        await DailyPayoutTestFixture.SeedOperatorAsync(db, 7);
        await DailyPayoutTestFixture.SeedTourScheduleAsync(db, 1, 1, 7, AtRunTime.AddDays(10));
        await DailyPayoutTestFixture.SeedConfirmedBookingAsync(db, 1, 1, 2_000_000m, AtRunTime.AddHours(-25));

        var gateway = new SimulatedPayoutTransferGateway(new FixedOutcomeScenario(PayoutTransferOutcome.Succeeded));
        var processor = CreateProcessor(db, gateway, AtRunTime);

        var first = await processor.RunIfDueAsync(CancellationToken.None);
        var second = await processor.RunIfDueAsync(CancellationToken.None);

        first.Should().Be(DailyPayoutRunOutcome.Ran);
        second.Should().Be(DailyPayoutRunOutcome.AlreadyRan, "PD-11: a restart/duplicate poll must not create a second run or batch");
        db.PayoutRuns.Should().HaveCount(1);
        db.PayoutBatches.Should().HaveCount(1);
    }

    [Fact]
    public async Task RunIfDueAsync_BookingOfLockedOperator_IsScheduledButNotTransferred()
    {
        // SRS 3.10.5 (BR-160): scheduling does not check the lock — the settlement/batch is still
        // created on time; only the transfer is deferred, leaving the batch Scheduled.
        await using var db = TestDbContext.Create();
        await DailyPayoutTestFixture.SeedOperatorAsync(db, 7, AccountStatus.Locked);
        await DailyPayoutTestFixture.SeedTourScheduleAsync(db, 1, 1, 7, AtRunTime.AddDays(10));
        await DailyPayoutTestFixture.SeedConfirmedBookingAsync(db, 1, 1, 2_000_000m, AtRunTime.AddHours(-25));

        var processor = CreateProcessor(db, new SimulatedPayoutTransferGateway(new FixedOutcomeScenario(PayoutTransferOutcome.Succeeded)), AtRunTime);
        await processor.RunIfDueAsync(CancellationToken.None);

        var batch = db.PayoutBatches.Should().ContainSingle().Subject;
        batch.Status.Should().Be(PayoutSettlementStatus.Scheduled);
        batch.AttemptCount.Should().Be(0, "no gateway call is ever made while the operator is Locked");
        db.PayoutSettlements.Should().ContainSingle().Which.Status.Should().Be(PayoutSettlementStatus.Scheduled);
    }

    [Fact]
    public async Task RunIfDueAsync_CancelledBooking_CreatesNoSettlement_ButLogsTheExclusion()
    {
        await using var db = TestDbContext.Create();
        await DailyPayoutTestFixture.SeedOperatorAsync(db, 7);
        await DailyPayoutTestFixture.SeedTourScheduleAsync(db, 1, 1, 7, AtRunTime.AddDays(10));
        var booking = await DailyPayoutTestFixture.SeedConfirmedBookingAsync(db, 1, 1, 2_000_000m, AtRunTime.AddHours(-25));
        booking.Cancel(AtRunTime.AddHours(-1), "Traveler requested");
        await db.SaveChangesAsync();

        var processor = CreateProcessor(db, new SimulatedPayoutTransferGateway(new FixedOutcomeScenario(PayoutTransferOutcome.Succeeded)), AtRunTime);
        await processor.RunIfDueAsync(CancellationToken.None);

        db.PayoutSettlements.Should().BeEmpty("PD-2A: a Cancelled booking gets no settlement, this or ever");
        db.AuditLogs.Should().ContainSingle(a =>
            a.ActionType == AuditActionTypes.PayoutExclusionCancelledBeforeRun && a.AffectedEntityId == booking.Id);
    }

    [Fact]
    public async Task RunIfDueAsync_CancelledBookingAlreadyLogged_DoesNotLogItAgain()
    {
        await using var db = TestDbContext.Create();
        await DailyPayoutTestFixture.SeedOperatorAsync(db, 7);
        await DailyPayoutTestFixture.SeedTourScheduleAsync(db, 1, 1, 7, AtRunTime.AddDays(10));
        var booking = await DailyPayoutTestFixture.SeedConfirmedBookingAsync(db, 1, 1, 2_000_000m, AtRunTime.AddHours(-25));
        booking.Cancel(AtRunTime.AddHours(-1), "Traveler requested");
        await db.SaveChangesAsync();

        var gateway = new SimulatedPayoutTransferGateway(new FixedOutcomeScenario(PayoutTransferOutcome.Succeeded));
        await CreateProcessor(db, gateway, AtRunTime).RunIfDueAsync(CancellationToken.None);
        await CreateProcessor(db, gateway, AtRunTime.AddDays(1)).RunIfDueAsync(CancellationToken.None);

        db.AuditLogs.Count(a => a.ActionType == AuditActionTypes.PayoutExclusionCancelledBeforeRun && a.AffectedEntityId == booking.Id)
            .Should().Be(1);
    }

    [Fact]
    public async Task RunIfDueAsync_TransferFailsPermanently_LeavesBatchFailed()
    {
        await using var db = TestDbContext.Create();
        await DailyPayoutTestFixture.SeedOperatorAsync(db, 7);
        await DailyPayoutTestFixture.SeedTourScheduleAsync(db, 1, 1, 7, AtRunTime.AddDays(10));
        await DailyPayoutTestFixture.SeedConfirmedBookingAsync(db, 1, 1, 2_000_000m, AtRunTime.AddHours(-25));

        var processor = CreateProcessor(db, new SimulatedPayoutTransferGateway(new FixedOutcomeScenario(PayoutTransferOutcome.Rejected)), AtRunTime);
        await processor.RunIfDueAsync(CancellationToken.None);

        var batch = db.PayoutBatches.Should().ContainSingle().Subject;
        batch.Status.Should().Be(PayoutSettlementStatus.SettlementFailed);
    }

    [Fact]
    public async Task RetrySweep_OperatorUnlockedLater_TransfersThePreviouslyScheduledBatch()
    {
        // BR-160: the batch created while Locked is never recreated (PD-11) — the next sweep
        // after the operator is unlocked transfers that SAME Scheduled batch.
        await using var db = TestDbContext.Create();
        var operatorUser = await DailyPayoutTestFixture.SeedOperatorAsync(db, 7, AccountStatus.Locked);
        await DailyPayoutTestFixture.SeedTourScheduleAsync(db, 1, 1, 7, AtRunTime.AddDays(10));
        await DailyPayoutTestFixture.SeedConfirmedBookingAsync(db, 1, 1, 2_000_000m, AtRunTime.AddHours(-25));
        var gateway = new SimulatedPayoutTransferGateway(new FixedOutcomeScenario(PayoutTransferOutcome.Succeeded));

        await CreateProcessor(db, gateway, AtRunTime).RunIfDueAsync(CancellationToken.None);
        var scheduledBatch = db.PayoutBatches.Should().ContainSingle().Subject;
        scheduledBatch.Status.Should().Be(PayoutSettlementStatus.Scheduled, "still locked right after being scheduled");

        operatorUser.Status = AccountStatus.Active;
        await db.SaveChangesAsync();
        var nextDay = AtRunTime.AddDays(1);
        var sweep = new DailyPayoutRetrySweepProcessor(db, gateway, new PayoutTestHelpers.FixedClock(nextDay), new DailyPayoutEligibilityReader(db));
        var acted = await sweep.SweepAsync(CancellationToken.None);

        acted.Should().Be(1);
        db.PayoutBatches.Should().ContainSingle().Which.Id.Should().Be(scheduledBatch.Id, "the same batch, never recreated");
        var settlement = db.PayoutSettlements.Should().ContainSingle().Subject;
        settlement.BookingId.Should().Be(1);
        settlement.Status.Should().Be(PayoutSettlementStatus.Settled);
    }

    [Fact]
    public async Task RunIfDueAsync_TransientFailure_ReturnsBatchToScheduledForTheRetrySweep()
    {
        // SRS 3.10.5, abnormal case 3.a1: a failed attempt rests as Scheduled (not Processing)
        // between hourly retries.
        await using var db = TestDbContext.Create();
        await DailyPayoutTestFixture.SeedOperatorAsync(db, 7);
        await DailyPayoutTestFixture.SeedTourScheduleAsync(db, 1, 1, 7, AtRunTime.AddDays(10));
        await DailyPayoutTestFixture.SeedConfirmedBookingAsync(db, 1, 1, 2_000_000m, AtRunTime.AddHours(-25));

        var processor = CreateProcessor(db, new SimulatedPayoutTransferGateway(new FixedOutcomeScenario(PayoutTransferOutcome.Timeout)), AtRunTime);
        await processor.RunIfDueAsync(CancellationToken.None);

        var batch = db.PayoutBatches.Should().ContainSingle().Subject;
        batch.Status.Should().Be(PayoutSettlementStatus.Scheduled);
        batch.AttemptCount.Should().Be(1);
    }

    // DAILY-PAYOUT-TRANSFER, BR-141: every paid booking enters a 24-hour Awaiting Window before
    // its Advance is eligible for any run, regardless of how long ago the run itself last fired.
    [Fact]
    public async Task RunIfDueAsync_BookingPaidLessThanTwentyFourHoursAgo_IsExcludedFromThisRun()
    {
        await using var db = TestDbContext.Create();
        await DailyPayoutTestFixture.SeedOperatorAsync(db, 7);
        await DailyPayoutTestFixture.SeedTourScheduleAsync(db, 1, 1, 7, AtRunTime.AddDays(10));
        await DailyPayoutTestFixture.SeedConfirmedBookingAsync(db, 1, 1, 2_000_000m, AtRunTime.AddHours(-2));

        var processor = CreateProcessor(db, new SimulatedPayoutTransferGateway(new FixedOutcomeScenario(PayoutTransferOutcome.Succeeded)), AtRunTime);
        var outcome = await processor.RunIfDueAsync(CancellationToken.None);

        outcome.Should().Be(DailyPayoutRunOutcome.Ran, "the run itself still happens — it simply has nothing eligible yet");
        db.PayoutBatches.Should().BeEmpty("BR-141: the booking is still inside its 24-hour Awaiting Window, and its tour has not ended");
        db.PayoutSettlements.Should().BeEmpty();
    }

    [Fact]
    public async Task RunIfDueAsync_BookingBecomesEligibleOnceItsWindowEnds_IncludedInTheFirstRunAfter()
    {
        await using var db = TestDbContext.Create();
        await DailyPayoutTestFixture.SeedOperatorAsync(db, 7);
        await DailyPayoutTestFixture.SeedTourScheduleAsync(db, 1, 1, 7, AtRunTime.AddDays(10));
        await DailyPayoutTestFixture.SeedConfirmedBookingAsync(db, 1, 1, 2_000_000m, AtRunTime.AddHours(-23));
        var gateway = new SimulatedPayoutTransferGateway(new FixedOutcomeScenario(PayoutTransferOutcome.Succeeded));

        await CreateProcessor(db, gateway, AtRunTime).RunIfDueAsync(CancellationToken.None);
        db.PayoutSettlements.Should().BeEmpty("still inside the 24-hour window on day 1");

        var nextDay = AtRunTime.AddDays(1); // now 24h + 1h since payment — the window has ended
        await CreateProcessor(db, gateway, nextDay).RunIfDueAsync(CancellationToken.None);

        var settlement = db.PayoutSettlements.Should().ContainSingle().Subject;
        settlement.BookingId.Should().Be(1);
        settlement.Status.Should().Be(PayoutSettlementStatus.Settled);
    }

    [Fact]
    public async Task RunIfDueAsync_TourEndsAfterAdvanceAlreadySettled_CreatesFinalOnFirstRunAfterTourEnd()
    {
        await using var db = TestDbContext.Create();
        await DailyPayoutTestFixture.SeedOperatorAsync(db, 7);
        // Tour ends the day after the run — Advance settles today, Final follows tomorrow (Q6).
        await DailyPayoutTestFixture.SeedTourScheduleAsync(db, 1, 1, 7, endAtUtc: AtRunTime.AddHours(20));
        await DailyPayoutTestFixture.SeedConfirmedBookingAsync(db, 1, 1, 2_000_000m, AtRunTime.AddHours(-25));
        var gateway = new SimulatedPayoutTransferGateway(new FixedOutcomeScenario(PayoutTransferOutcome.Succeeded));

        await CreateProcessor(db, gateway, AtRunTime).RunIfDueAsync(CancellationToken.None);
        db.PayoutSettlements.Should().ContainSingle(s => s.Kind == PayoutSettlementKind.Advance);
        db.PayoutSettlements.Should().NotContain(s => s.Kind == PayoutSettlementKind.Final, "the tour has not ended yet");

        var nextDay = AtRunTime.AddDays(1); // the tour has now ended
        await CreateProcessor(db, gateway, nextDay).RunIfDueAsync(CancellationToken.None);

        db.PayoutSettlements.Should().HaveCount(2);
        db.PayoutSettlements.Should().ContainSingle(s => s.Kind == PayoutSettlementKind.Final && s.PayoutAmount == 400_000m);
    }
}