using FluentAssertions;

using TripMate.Application.Features.Payouts.DailySettlement;
using TripMate.Application.UnitTests.TestUtilities;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Application.UnitTests.Features.Payouts;

// DAILY-PAYOUT-TRANSFER, Task 6/6A: the daily run (PD-1, PD-2, PD-11, PD-14, PD-16).
public sealed class DailyPayoutRunProcessorTests
{
    // 10:30 Vietnam time on 2026-11-09 -> 03:30 UTC.
    private static readonly DateTimeOffset AtRunTime = new(2026, 11, 9, 3, 30, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset BeforeRunTime = new(2026, 11, 9, 1, 0, 0, TimeSpan.Zero); // 08:00 VN

    private static DailyPayoutRunProcessor CreateProcessor(
        TestDbContext db, IPayoutTransferGateway gateway, DateTimeOffset nowUtc) =>
        new(db, new DailyPayoutEligibilityReader(db), gateway, new PayoutTestHelpers.FixedClock(nowUtc));

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
    public async Task RunIfDueAsync_WithOneEligibleBooking_CreatesSettledBatch()
    {
        await using var db = TestDbContext.Create();
        var operatorUser = await DailyPayoutTestFixture.SeedOperatorAsync(db, 7);
        await DailyPayoutTestFixture.SeedTourScheduleAsync(db, tourId: 1, scheduleId: 1, operatorId: 7, endAtUtc: AtRunTime.AddDays(-1));
        await DailyPayoutTestFixture.SeedConfirmedBookingAsync(db, bookingId: 1, scheduleId: 1, totalAmount: 2_000_000m, succeededAtUtc: AtRunTime.AddHours(-2));

        var processor = CreateProcessor(db, new SimulatedPayoutTransferGateway(new FixedOutcomeScenario(PayoutTransferOutcome.Succeeded)), AtRunTime);
        var outcome = await processor.RunIfDueAsync(CancellationToken.None);

        outcome.Should().Be(DailyPayoutRunOutcome.Ran);
        db.PayoutRuns.Should().ContainSingle(r => r.Status == PayoutRun.StatusCompleted);

        var batch = db.PayoutBatches.Should().ContainSingle().Subject;
        batch.TourOperatorUserId.Should().Be(7);
        batch.Status.Should().Be(PayoutSettlementStatus.Settled);
        batch.TotalAmount.Should().Be(1_800_000m);

        var settlement = db.PayoutSettlements.Should().ContainSingle().Subject;
        settlement.BookingId.Should().Be(1);
        settlement.Kind.Should().Be(PayoutSettlementKind.Main);
        settlement.PayoutAmount.Should().Be(1_800_000m);
        settlement.Status.Should().Be(PayoutSettlementStatus.Settled);
    }

    [Fact]
    public async Task RunIfDueAsync_CalledTwiceSameDay_IsIdempotent()
    {
        await using var db = TestDbContext.Create();
        await DailyPayoutTestFixture.SeedOperatorAsync(db, 7);
        await DailyPayoutTestFixture.SeedTourScheduleAsync(db, 1, 1, 7, AtRunTime.AddDays(-1));
        await DailyPayoutTestFixture.SeedConfirmedBookingAsync(db, 1, 1, 2_000_000m, AtRunTime.AddHours(-2));

        var gateway = new SimulatedPayoutTransferGateway(new FixedOutcomeScenario(PayoutTransferOutcome.Succeeded));
        var processor = CreateProcessor(db, gateway, AtRunTime);

        var first = await processor.RunIfDueAsync(CancellationToken.None);
        var second = await processor.RunIfDueAsync(CancellationToken.None);

        first.Should().Be(DailyPayoutRunOutcome.Ran);
        second.Should().Be(DailyPayoutRunOutcome.AlreadyRan, "PD-16: a restart/duplicate poll must not create a second run or batch");
        db.PayoutRuns.Should().HaveCount(1);
        db.PayoutBatches.Should().HaveCount(1);
    }

    [Fact]
    public async Task RunIfDueAsync_BookingOfLockedOperator_IsNotSelected()
    {
        await using var db = TestDbContext.Create();
        await DailyPayoutTestFixture.SeedOperatorAsync(db, 7, AccountStatus.Locked);
        await DailyPayoutTestFixture.SeedTourScheduleAsync(db, 1, 1, 7, AtRunTime.AddDays(-1));
        await DailyPayoutTestFixture.SeedConfirmedBookingAsync(db, 1, 1, 2_000_000m, AtRunTime.AddHours(-2));

        var processor = CreateProcessor(db, new SimulatedPayoutTransferGateway(new FixedOutcomeScenario(PayoutTransferOutcome.Succeeded)), AtRunTime);
        await processor.RunIfDueAsync(CancellationToken.None);

        db.PayoutBatches.Should().BeEmpty("BR-160/PD-9: a locked operator's booking is not settled while locked");
        db.PayoutSettlements.Should().BeEmpty();
    }

    [Fact]
    public async Task RunIfDueAsync_CancelledBookingWithPartialRefund_CreatesRetainedSettlement()
    {
        await using var db = TestDbContext.Create();
        await DailyPayoutTestFixture.SeedOperatorAsync(db, 7);
        await DailyPayoutTestFixture.SeedTourScheduleAsync(db, 1, 1, 7, AtRunTime.AddDays(1));
        // 2,000,000 total, 80% (1,600,000) refunded -> 400,000 retained.
        await DailyPayoutTestFixture.SeedCancelledPartiallyRefundedBookingAsync(
            db, bookingId: 1, scheduleId: 1, totalAmount: 2_000_000m, refundedAmount: 1_600_000m,
            succeededAtUtc: AtRunTime.AddDays(-10), cancelledAtUtc: AtRunTime.AddHours(-1));

        var processor = CreateProcessor(db, new SimulatedPayoutTransferGateway(new FixedOutcomeScenario(PayoutTransferOutcome.Succeeded)), AtRunTime);
        await processor.RunIfDueAsync(CancellationToken.None);

        var settlement = db.PayoutSettlements.Should().ContainSingle().Subject;
        settlement.Kind.Should().Be(PayoutSettlementKind.Retained);
        settlement.GrossAmount.Should().Be(400_000m);
        settlement.CommissionAmount.Should().Be(40_000m);
        settlement.PayoutAmount.Should().Be(360_000m);
    }

    [Fact]
    public async Task RunIfDueAsync_FullyRefundedCancelledBooking_CreatesNoSettlement()
    {
        await using var db = TestDbContext.Create();
        await DailyPayoutTestFixture.SeedOperatorAsync(db, 7);
        await DailyPayoutTestFixture.SeedTourScheduleAsync(db, 1, 1, 7, AtRunTime.AddDays(1));
        await DailyPayoutTestFixture.SeedCancelledPartiallyRefundedBookingAsync(
            db, 1, 1, totalAmount: 2_000_000m, refundedAmount: 2_000_000m,
            succeededAtUtc: AtRunTime.AddDays(-10), cancelledAtUtc: AtRunTime.AddHours(-1));

        var processor = CreateProcessor(db, new SimulatedPayoutTransferGateway(new FixedOutcomeScenario(PayoutTransferOutcome.Succeeded)), AtRunTime);
        await processor.RunIfDueAsync(CancellationToken.None);

        db.PayoutSettlements.Should().BeEmpty("a 100% refund leaves nothing for the operator (BR-142)");
    }

    [Fact]
    public async Task RunIfDueAsync_TransferFailsPermanently_LeavesBatchFailed()
    {
        await using var db = TestDbContext.Create();
        await DailyPayoutTestFixture.SeedOperatorAsync(db, 7);
        await DailyPayoutTestFixture.SeedTourScheduleAsync(db, 1, 1, 7, AtRunTime.AddDays(-1));
        await DailyPayoutTestFixture.SeedConfirmedBookingAsync(db, 1, 1, 2_000_000m, AtRunTime.AddHours(-2));

        var processor = CreateProcessor(db, new SimulatedPayoutTransferGateway(new FixedOutcomeScenario(PayoutTransferOutcome.Rejected)), AtRunTime);
        await processor.RunIfDueAsync(CancellationToken.None);

        var batch = db.PayoutBatches.Should().ContainSingle().Subject;
        batch.Status.Should().Be(PayoutSettlementStatus.SettlementFailed);
    }

    [Fact]
    public async Task RunIfDueAsync_OperatorUnlockedLater_PicksUpThePreviouslySkippedBooking()
    {
        await using var db = TestDbContext.Create();
        var operatorUser = await DailyPayoutTestFixture.SeedOperatorAsync(db, 7, AccountStatus.Locked);
        await DailyPayoutTestFixture.SeedTourScheduleAsync(db, 1, 1, 7, AtRunTime.AddDays(-1));
        await DailyPayoutTestFixture.SeedConfirmedBookingAsync(db, 1, 1, 2_000_000m, AtRunTime.AddHours(-2));
        var gateway = new SimulatedPayoutTransferGateway(new FixedOutcomeScenario(PayoutTransferOutcome.Succeeded));

        await CreateProcessor(db, gateway, AtRunTime).RunIfDueAsync(CancellationToken.None);
        db.PayoutSettlements.Should().BeEmpty("still locked on day 1");

        operatorUser.Status = AccountStatus.Active;
        await db.SaveChangesAsync();
        var nextDay = AtRunTime.AddDays(1);
        await CreateProcessor(db, gateway, nextDay).RunIfDueAsync(CancellationToken.None);

        var settlement = db.PayoutSettlements.Should().ContainSingle().Subject;
        settlement.BookingId.Should().Be(1);
        settlement.Status.Should().Be(PayoutSettlementStatus.Settled);
    }

    [Fact]
    public async Task RunIfDueAsync_TransientFailure_LeavesBatchProcessingForTheRetrySweep()
    {
        await using var db = TestDbContext.Create();
        await DailyPayoutTestFixture.SeedOperatorAsync(db, 7);
        await DailyPayoutTestFixture.SeedTourScheduleAsync(db, 1, 1, 7, AtRunTime.AddDays(-1));
        await DailyPayoutTestFixture.SeedConfirmedBookingAsync(db, 1, 1, 2_000_000m, AtRunTime.AddHours(-2));

        var processor = CreateProcessor(db, new SimulatedPayoutTransferGateway(new FixedOutcomeScenario(PayoutTransferOutcome.Timeout)), AtRunTime);
        await processor.RunIfDueAsync(CancellationToken.None);

        var batch = db.PayoutBatches.Should().ContainSingle().Subject;
        batch.Status.Should().Be(PayoutSettlementStatus.Processing);
        batch.AttemptCount.Should().Be(1);
    }
}
