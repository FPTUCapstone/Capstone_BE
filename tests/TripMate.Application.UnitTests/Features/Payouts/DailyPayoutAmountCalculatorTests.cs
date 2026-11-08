using FluentAssertions;

using TripMate.Application.Features.Payouts.DailySettlement;
using TripMate.Domain.Entities;

namespace TripMate.Application.UnitTests.Features.Payouts;

// DAILY-PAYOUT-TRANSFER, Task 4 (PD-3, PD-4, PD-15, PD-2).
public sealed class DailyPayoutAmountCalculatorTests
{
    [Fact]
    public void Calculate_SimpleBooking_ComputesCommissionAndNet()
    {
        var input = new DailySettlementInput(BookingId: 1, Kind: PayoutSettlementKind.Main, GrossAmount: 2_000_000m, CommissionRatePercent: 10m);

        var batch = DailyPayoutAmountCalculator.Calculate([input], outstandingDebt: 0m);

        batch.Settlements.Should().ContainSingle();
        var result = batch.Settlements[0];
        result.CommissionAmount.Should().Be(200_000m);
        result.NetAmount.Should().Be(1_800_000m);
        result.DebtOffsetAmount.Should().Be(0m);
        result.PayoutAmount.Should().Be(1_800_000m);
        result.IsExcluded.Should().BeFalse();
        batch.RemainingDebt.Should().Be(0m);
    }

    [Fact]
    public void Calculate_CommissionRounding_FloorsToWholeVnd()
    {
        // 100,001 * 10% = 10,000.1 -> floored to 10,000 (BR-146/PD-3: rounded down to whole VND).
        var input = new DailySettlementInput(1, PayoutSettlementKind.Main, 100_001m, 10m);

        var batch = DailyPayoutAmountCalculator.Calculate([input], 0m);

        batch.Settlements[0].CommissionAmount.Should().Be(10_000m);
        batch.Settlements[0].NetAmount.Should().Be(90_001m);
    }

    [Fact]
    public void Calculate_DebtEqualToNet_ZeroesPayoutAndExcludes()
    {
        var input = new DailySettlementInput(1, PayoutSettlementKind.Main, 2_000_000m, 10m); // net 1,800,000

        var batch = DailyPayoutAmountCalculator.Calculate([input], outstandingDebt: 1_800_000m);

        batch.Settlements[0].DebtOffsetAmount.Should().Be(1_800_000m);
        batch.Settlements[0].PayoutAmount.Should().Be(0m);
        batch.Settlements[0].IsExcluded.Should().BeTrue();
        batch.RemainingDebt.Should().Be(0m);
    }

    [Fact]
    public void Calculate_DebtSpansMultipleBookings_ConsumesInOrderThenLeavesRemainder()
    {
        // PD-15: debt is deducted in order across the operator's settlements in this run.
        var inputs = new[]
        {
            new DailySettlementInput(1, PayoutSettlementKind.Main, 1_000_000m, 10m), // net 900,000
            new DailySettlementInput(2, PayoutSettlementKind.Main, 1_000_000m, 10m), // net 900,000
        };

        var batch = DailyPayoutAmountCalculator.Calculate(inputs, outstandingDebt: 1_500_000m);

        batch.Settlements[0].DebtOffsetAmount.Should().Be(900_000m);
        batch.Settlements[0].PayoutAmount.Should().Be(0m);
        batch.Settlements[0].IsExcluded.Should().BeTrue();

        batch.Settlements[1].DebtOffsetAmount.Should().Be(600_000m);
        batch.Settlements[1].PayoutAmount.Should().Be(300_000m);
        batch.Settlements[1].IsExcluded.Should().BeFalse();

        batch.RemainingDebt.Should().Be(0m);
    }

    [Fact]
    public void Calculate_DebtExceedsAllNetAmounts_LeavesRemainderForNextRun()
    {
        var input = new DailySettlementInput(1, PayoutSettlementKind.Main, 1_000_000m, 10m); // net 900,000

        var batch = DailyPayoutAmountCalculator.Calculate([input], outstandingDebt: 2_000_000m);

        batch.Settlements[0].DebtOffsetAmount.Should().Be(900_000m);
        batch.Settlements[0].PayoutAmount.Should().Be(0m);
        batch.RemainingDebt.Should().Be(1_100_000m);
    }

    [Fact]
    public void Calculate_RetainedKindAfterPartialRefund_IsComputedLikeAnyOtherSettlement()
    {
        // BR-142: the booking was cancelled with an 80% refund; 20% (400,000) is retained.
        var input = new DailySettlementInput(1, PayoutSettlementKind.Retained, GrossAmount: 400_000m, CommissionRatePercent: 10m);

        var batch = DailyPayoutAmountCalculator.Calculate([input], 0m);

        batch.Settlements[0].Kind.Should().Be(PayoutSettlementKind.Retained);
        batch.Settlements[0].CommissionAmount.Should().Be(40_000m);
        batch.Settlements[0].PayoutAmount.Should().Be(360_000m);
    }

    [Fact]
    public void Calculate_NegativeDebt_Throws()
    {
        var input = new DailySettlementInput(1, PayoutSettlementKind.Main, 1_000_000m, 10m);

        var act = () => DailyPayoutAmountCalculator.Calculate([input], outstandingDebt: -1m);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }
}
