using TripMate.Application.Features.Bookings.Refunds;

namespace TripMate.Application.UnitTests.Features.Bookings.Refunds;

// BR-139 (SRS 3.8.4.3, spec v1.2): the Tour Operator's return share of a refund = refund amount *
// amount already transferred to the Tour Operator for the booking / booking PaidAmount, rounded
// down to whole VND: 0 before the Advance settlement, 70% after it, 90% after the Final
// settlement. TripMate pays the remainder of the refund from the amount it retains.
public sealed class RefundShareCalculatorTests
{
    [Fact]
    public void CalculateOperatorReturnShare_BeforeAdvanceSettlement_IsZero()
    {
        decimal share = RefundShareCalculator.CalculateOperatorReturnShare(
            refundAmount: 500_000m, transferredToOperatorAmount: 0m, paidAmount: 1_000_000m);

        Assert.Equal(0m, share);
    }

    [Fact]
    public void CalculateOperatorReturnShare_AfterAdvanceSettlement_IsSeventyPercentOfRefund()
    {
        // Advance settled: 70% of PaidAmount already transferred.
        decimal share = RefundShareCalculator.CalculateOperatorReturnShare(
            refundAmount: 500_000m, transferredToOperatorAmount: 700_000m, paidAmount: 1_000_000m);

        Assert.Equal(350_000m, share);
    }

    [Fact]
    public void CalculateOperatorReturnShare_AfterFinalSettlement_IsNinetyPercentOfRefund()
    {
        // Advance + Final settled: 90% of PaidAmount already transferred.
        decimal share = RefundShareCalculator.CalculateOperatorReturnShare(
            refundAmount: 500_000m, transferredToOperatorAmount: 900_000m, paidAmount: 1_000_000m);

        Assert.Equal(450_000m, share);
    }

    [Fact]
    public void CalculateOperatorReturnShare_RoundsDownToWholeVnd()
    {
        decimal share = RefundShareCalculator.CalculateOperatorReturnShare(
            refundAmount: 333_333m, transferredToOperatorAmount: 700_000m, paidAmount: 1_000_000m);

        // 333_333 * 0.7 = 233_333.1 -> floored to 233_333, never rounded up.
        Assert.Equal(233_333m, share);
    }

    [Fact]
    public void CalculateOperatorReturnShare_ZeroRefundAmount_IsZero()
    {
        decimal share = RefundShareCalculator.CalculateOperatorReturnShare(
            refundAmount: 0m, transferredToOperatorAmount: 700_000m, paidAmount: 1_000_000m);

        Assert.Equal(0m, share);
    }

    [Fact]
    public void CalculateOperatorReturnShare_NegativeRefundAmount_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            RefundShareCalculator.CalculateOperatorReturnShare(-1m, 700_000m, 1_000_000m));
    }

    [Fact]
    public void CalculateOperatorReturnShare_NegativeTransferredAmount_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            RefundShareCalculator.CalculateOperatorReturnShare(500_000m, -1m, 1_000_000m));
    }

    [Fact]
    public void CalculateOperatorReturnShare_ZeroOrNegativePaidAmount_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            RefundShareCalculator.CalculateOperatorReturnShare(500_000m, 700_000m, 0m));
    }
}