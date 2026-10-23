using FluentAssertions;

using TripMate.Application.Features.Operator.Payouts.Common;

namespace TripMate.Application.UnitTests.Features.Operator.Payouts.Common;

public sealed class OperatorPayoutCalculatorTests
{
    [Fact]
    public void Calculate_PaymentOnly_NoRefundsNoAdjustments()
    {
        var payments = new[]
        {
            new PayoutPaymentInput(BookingId: 1, PaymentTransactionId: 101, PaidAmount: 1_000_000m,
                DiscountAmount: 0m, CommissionRateApplied: 10m, ParticipantCount: 2),
        };

        var result = OperatorPayoutCalculator.Calculate(payments, [], []);

        result.PaymentsReceived.Should().Be(1_000_000m);
        result.RefundedAmount.Should().Be(0m);
        result.NetSales.Should().Be(1_000_000m);
        result.CommissionAmount.Should().Be(100_000m);
        result.PayableNetBeforeAdjustments.Should().Be(900_000m);
        result.AdjustmentsApplied.Should().Be(0m);
        result.PayableNetAmount.Should().Be(900_000m);
        result.BookingCount.Should().Be(1);
        result.ParticipantCount.Should().Be(2);
        result.FaultyBookingIds.Should().BeEmpty();
    }

    [Fact]
    public void Calculate_PartialRefundOnOneBooking_ReducesNetAndCommissionForThatBookingOnly()
    {
        var payments = new[]
        {
            new PayoutPaymentInput(1, 101, 1_000_000m, 0m, 10m, 2),
            new PayoutPaymentInput(2, 102, 2_000_000m, 0m, 10m, 4),
        };
        var refunds = new[]
        {
            new PayoutRefundInput(RefundId: 501, BookingId: 1, RefundedAmount: 300_000m, CommissionRateApplied: 10m),
        };

        var result = OperatorPayoutCalculator.Calculate(payments, refunds, []);

        result.PaymentsReceived.Should().Be(3_000_000m);
        result.RefundedAmount.Should().Be(300_000m);
        result.NetSales.Should().Be(2_700_000m);
        // Booking 1 commission: 100,000 - round(300,000*10%)=30,000 => 70,000
        // Booking 2 commission: 200,000 (untouched)
        result.CommissionAmount.Should().Be(270_000m);
        result.PayableNetBeforeAdjustments.Should().Be(2_430_000m);
        result.PayableNetAmount.Should().Be(2_430_000m);

        var detailA = result.Items.Single(item => item.BookingId == 1);
        detailA.CommissionAmount.Should().Be(70_000m);
        var detailB = result.Items.Single(item => item.BookingId == 2);
        detailB.CommissionAmount.Should().Be(200_000m);

        result.RefundItems.Should().ContainSingle();
        result.RefundItems[0].CommissionReduction.Should().Be(30_000m);
        result.FaultyBookingIds.Should().BeEmpty();
    }

    [Fact]
    public void Calculate_FullRefundOnBooking_YieldsZeroCommissionNeverNegative()
    {
        var payments = new[]
        {
            new PayoutPaymentInput(1, 101, 1_000_000m, 0m, 10m, 2),
        };
        var refunds = new[]
        {
            new PayoutRefundInput(501, 1, 1_000_000m, 10m),
        };

        var result = OperatorPayoutCalculator.Calculate(payments, refunds, []);

        result.NetSales.Should().Be(0m);
        result.CommissionAmount.Should().Be(0m);
        result.PayableNetBeforeAdjustments.Should().Be(0m);
        result.Items.Single().CommissionAmount.Should().Be(0m);
        result.FaultyBookingIds.Should().BeEmpty();
    }

    [Fact]
    public void Calculate_RefundExceedingPayment_IsReportedAsFaultyBooking()
    {
        var payments = new[]
        {
            new PayoutPaymentInput(1, 101, 1_000_000m, 0m, 10m, 2),
        };
        var refunds = new[]
        {
            new PayoutRefundInput(501, 1, 1_200_000m, 10m),
        };

        var result = OperatorPayoutCalculator.Calculate(payments, refunds, []);

        result.FaultyBookingIds.Should().ContainSingle().Which.Should().Be(1);
    }

    [Fact]
    public void Calculate_MultiplePaymentRatesPerBooking_UsesEachPaymentsOwnRate()
    {
        var payments = new[]
        {
            new PayoutPaymentInput(1, 101, 1_000_000m, 0m, 10m, 2),
            new PayoutPaymentInput(2, 102, 1_000_000m, 0m, 15m, 2),
        };

        var result = OperatorPayoutCalculator.Calculate(payments, [], []);

        result.Items.Single(item => item.BookingId == 1).CommissionAmount.Should().Be(100_000m);
        result.Items.Single(item => item.BookingId == 2).CommissionAmount.Should().Be(150_000m);
        result.CommissionAmount.Should().Be(250_000m);
    }

    [Fact]
    public void Calculate_OutstandingAdjustmentFullyAbsorbed_ReducesPayableByFullAmount()
    {
        var payments = new[]
        {
            new PayoutPaymentInput(1, 101, 1_000_000m, 0m, 10m, 2),
        };
        // Payable before adjustments: 900,000. An outstanding adjustment of -200,000 should be
        // fully applied since it is smaller than the payable amount.
        var adjustments = new[] { -200_000m };

        var result = OperatorPayoutCalculator.Calculate(payments, [], adjustments);

        result.PayableNetBeforeAdjustments.Should().Be(900_000m);
        result.AdjustmentsApplied.Should().Be(200_000m);
        result.PayableNetAmount.Should().Be(700_000m);
    }

    [Fact]
    public void Calculate_OutstandingAdjustmentsExceedPayable_NeverGoesBelowZero_RemainderCarriesForward()
    {
        var payments = new[]
        {
            new PayoutPaymentInput(1, 101, 500_000m, 0m, 10m, 1),
        };
        // Payable before adjustments: 450,000. Adjustments total -600,000: only 450,000 can be
        // absorbed here; the remaining -150,000 must stay outstanding for the caller to track
        // (the calculator itself does not decide which adjustment rows absorbed how much —
        // that bookkeeping is the caller's job; this only proves the total never goes negative).
        var adjustments = new[] { -600_000m };

        var result = OperatorPayoutCalculator.Calculate(payments, [], adjustments);

        result.PayableNetBeforeAdjustments.Should().Be(450_000m);
        result.AdjustmentsApplied.Should().Be(450_000m);
        result.PayableNetAmount.Should().Be(0m);
    }

    [Fact]
    public void Calculate_MultipleSmallAdjustments_AppliesInOrderUntilPayableExhausted()
    {
        var payments = new[]
        {
            new PayoutPaymentInput(1, 101, 1_000_000m, 0m, 10m, 1),
        };
        // Payable before adjustments: 900,000.
        var adjustments = new[] { -300_000m, -300_000m, -300_000m, -300_000m };

        var result = OperatorPayoutCalculator.Calculate(payments, [], adjustments);

        result.AdjustmentsApplied.Should().Be(900_000m);
        result.PayableNetAmount.Should().Be(0m);
    }

    [Fact]
    public void Calculate_EmptyBookingSet_ReturnsZeroedResult()
    {
        var result = OperatorPayoutCalculator.Calculate([], [], []);

        result.PaymentsReceived.Should().Be(0m);
        result.PayableNetAmount.Should().Be(0m);
        result.BookingCount.Should().Be(0);
        result.Items.Should().BeEmpty();
    }
}