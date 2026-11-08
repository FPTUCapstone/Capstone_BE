using FluentAssertions;

using TripMate.Domain.Entities;

namespace TripMate.Application.UnitTests.Features.Payouts.Domain;

// DAILY-PAYOUT-TRANSFER, Task 3: PayoutRun lifecycle and PayoutSettlementAdjustment (PD-10, PD-11).
public sealed class PayoutRunAndAdjustmentTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 8, 3, 0, 0, TimeSpan.Zero);

    [Fact]
    public void PayoutRun_Start_IsRunning()
    {
        var run = PayoutRun.Start(new DateOnly(2026, 11, 8), T0);

        run.Status.Should().Be(PayoutRun.StatusRunning);
        run.RunDate.Should().Be(new DateOnly(2026, 11, 8));
        run.CompletedAtUtc.Should().BeNull();
    }

    [Fact]
    public void PayoutRun_Complete_SetsStatusAndTimestamp()
    {
        var run = PayoutRun.Start(new DateOnly(2026, 11, 8), T0);

        run.Complete(T0.AddMinutes(5));

        run.Status.Should().Be(PayoutRun.StatusCompleted);
        run.CompletedAtUtc.Should().Be(T0.AddMinutes(5));
    }

    [Fact]
    public void PayoutRun_Fail_SetsStatusAndTimestamp()
    {
        var run = PayoutRun.Start(new DateOnly(2026, 11, 8), T0);

        run.Fail(T0.AddMinutes(5));

        run.Status.Should().Be(PayoutRun.StatusFailed);
    }

    [Fact]
    public void PayoutSettlementAdjustment_CreateCarryOver_IsOutstandingUntilApplied()
    {
        var adjustment = PayoutSettlementAdjustment.CreateCarryOver(
            sourceSettlementId: 1, tourOperatorUserId: 7, amount: 1_800_000m, createdAtUtc: T0);

        adjustment.IsOutstanding.Should().BeTrue();
        adjustment.Amount.Should().Be(1_800_000m);

        adjustment.Apply(batchId: 99, appliedAtUtc: T0.AddDays(1));

        adjustment.IsOutstanding.Should().BeFalse();
        adjustment.AppliedBatchId.Should().Be(99);
    }

    [Fact]
    public void PayoutSettlementAdjustment_Apply_Twice_Throws()
    {
        var adjustment = PayoutSettlementAdjustment.CreateCarryOver(1, 7, 1_800_000m, T0);
        adjustment.Apply(99, T0.AddDays(1));

        var act = () => adjustment.Apply(100, T0.AddDays(2));

        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void PayoutSettlementAdjustment_NonPositiveAmount_Throws()
    {
        var act = () => PayoutSettlementAdjustment.CreateCarryOver(1, 7, 0m, T0);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void PayoutSettlement_MarkCarriedOverStandalone_SetsStatusWithoutTouchingAmount()
    {
        var settlement = PayoutSettlement.Create(
            batchId: 1, bookingId: 1, tourOperatorUserId: 7, runDate: new DateOnly(2026, 11, 8),
            kind: PayoutSettlementKind.Main, grossAmount: 2_000_000m, commissionRate: 10m,
            commissionAmount: 200_000m, netAmount: 1_800_000m, debtOffsetAmount: 0m,
            payoutAmount: 1_800_000m, createdAtUtc: T0);

        settlement.MarkCarriedOverStandalone(T0.AddDays(1));

        settlement.Status.Should().Be(PayoutSettlementStatus.CarriedOver);
        settlement.PayoutAmount.Should().Be(1_800_000m);
    }
}
