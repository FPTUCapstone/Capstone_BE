using FluentAssertions;

using TripMate.Application.Features.Payouts.DailySettlement;
using TripMate.Domain.Entities;

namespace TripMate.Application.UnitTests.Features.Payouts;

// DAILY-PAYOUT-TRANSFER, Task 3 (spec v6, PD-3, PD-4, PD-15).
public sealed class DailyPayoutAmountCalculatorTests
{
    [Fact]
    public void Calculate_Advance_IsSeventyPercentOfPaidAmount()
    {
        var input = new DailySettlementInput(BookingId: 1, Kind: PayoutSettlementKind.Advance, PaidAmount: 2_000_000m, RefundAmount: 0m, CommissionRatePercent: 10m);

        var batch = DailyPayoutAmountCalculator.Calculate([input], outstandingDebt: 0m);

        batch.Settlements.Should().ContainSingle();
        var result = batch.Settlements[0];
        result.GrossAmount.Should().Be(1_400_000m);
        result.CommissionAmount.Should().Be(0m);
        result.NetAmount.Should().Be(1_400_000m);
        result.DebtOffsetAmount.Should().Be(0m);
        result.PayoutAmount.Should().Be(1_400_000m);
        result.IsExcluded.Should().BeFalse();
        batch.RemainingDebt.Should().Be(0m);
    }

    [Fact]
    public void Calculate_Final_IsTwentyPercentOfPaidAmountLessRefund()
    {
        // RefundAmount is always 0 in this branch's scope, but the formula still subtracts it.
        var input = new DailySettlementInput(1, PayoutSettlementKind.Final, PaidAmount: 2_000_000m, RefundAmount: 0m, CommissionRatePercent: 10m);

        var batch = DailyPayoutAmountCalculator.Calculate([input], 0m);

        batch.Settlements[0].GrossAmount.Should().Be(400_000m);
        batch.Settlements[0].PayoutAmount.Should().Be(400_000m);
    }

    [Fact]
    public void Calculate_AdvanceAndFinalRounding_FloorsToWholeVndAndTripMateKeepsTheRemainder()
    {
        // 1,000,001 * 70% = 700,000.7 -> floored to 700,000; * 20% = 200,000.2 -> floored to
        // 200,000. The 1 VND fractional remainder of each line stays with TripMate (PD-3).
        var advance = new DailySettlementInput(1, PayoutSettlementKind.Advance, 1_000_001m, 0m, 10m);
        var final = new DailySettlementInput(2, PayoutSettlementKind.Final, 1_000_001m, 0m, 10m);

        var batch = DailyPayoutAmountCalculator.Calculate([advance, final], 0m);

        batch.Settlements[0].PayoutAmount.Should().Be(700_000m);
        batch.Settlements[1].PayoutAmount.Should().Be(200_000m);
    }

    [Fact]
    public void CalculateCommission_IsInformationalAndFloorsToWholeVnd()
    {
        // Not used in settlement math (v6): recorded only for future reporting (BR-140).
        DailyPayoutAmountCalculator.CalculateCommission(1_000_001m, 10m).Should().Be(100_000m);
    }

    [Fact]
    public void Calculate_DebtEqualToSettlementAmount_ZeroesPayoutAndExcludes()
    {
        var input = new DailySettlementInput(1, PayoutSettlementKind.Advance, 2_000_000m, 0m, 10m); // 1,400,000

        var batch = DailyPayoutAmountCalculator.Calculate([input], outstandingDebt: 1_400_000m);

        batch.Settlements[0].DebtOffsetAmount.Should().Be(1_400_000m);
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
            new DailySettlementInput(1, PayoutSettlementKind.Advance, 1_000_000m, 0m, 10m), // 700,000
            new DailySettlementInput(2, PayoutSettlementKind.Advance, 1_000_000m, 0m, 10m), // 700,000
        };

        var batch = DailyPayoutAmountCalculator.Calculate(inputs, outstandingDebt: 1_200_000m);

        batch.Settlements[0].DebtOffsetAmount.Should().Be(700_000m);
        batch.Settlements[0].PayoutAmount.Should().Be(0m);
        batch.Settlements[0].IsExcluded.Should().BeTrue();

        batch.Settlements[1].DebtOffsetAmount.Should().Be(500_000m);
        batch.Settlements[1].PayoutAmount.Should().Be(200_000m);
        batch.Settlements[1].IsExcluded.Should().BeFalse();

        batch.RemainingDebt.Should().Be(0m);
    }

    [Fact]
    public void Calculate_DebtExceedsAllSettlementAmounts_LeavesRemainderForNextRun()
    {
        var input = new DailySettlementInput(1, PayoutSettlementKind.Advance, 1_000_000m, 0m, 10m); // 700,000

        var batch = DailyPayoutAmountCalculator.Calculate([input], outstandingDebt: 2_000_000m);

        batch.Settlements[0].DebtOffsetAmount.Should().Be(700_000m);
        batch.Settlements[0].PayoutAmount.Should().Be(0m);
        batch.RemainingDebt.Should().Be(1_300_000m);
    }

    [Fact]
    public void Calculate_NegativeDebt_Throws()
    {
        var input = new DailySettlementInput(1, PayoutSettlementKind.Advance, 1_000_000m, 0m, 10m);

        var act = () => DailyPayoutAmountCalculator.Calculate([input], outstandingDebt: -1m);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Calculate_NegativePaidAmount_Throws()
    {
        var input = new DailySettlementInput(1, PayoutSettlementKind.Advance, -1m, 0m, 10m);

        var act = () => DailyPayoutAmountCalculator.Calculate([input], outstandingDebt: 0m);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Calculate_UnknownKind_Throws()
    {
        var input = new DailySettlementInput(1, "Main", 1_000_000m, 0m, 10m);

        var act = () => DailyPayoutAmountCalculator.Calculate([input], outstandingDebt: 0m);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }
}
