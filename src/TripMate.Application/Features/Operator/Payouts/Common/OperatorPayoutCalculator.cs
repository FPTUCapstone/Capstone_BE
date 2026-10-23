namespace TripMate.Application.Features.Operator.Payouts.Common;

/// <summary>One eligible booking's canonical successful payment, as fed to <see cref="OperatorPayoutCalculator"/>.</summary>
public sealed record PayoutPaymentInput(
    long BookingId,
    long PaymentTransactionId,
    decimal PaidAmount,
    decimal DiscountAmount,
    decimal CommissionRateApplied,
    int ParticipantCount);

/// <summary>A processed refund against one of the bookings in the same calculation, already
/// filtered by the caller to ProcessedAtUtc &lt;= cut-off (anything later is a
/// <see cref="TripMate.Domain.Entities.PayoutAdjustment"/>, not an input here).</summary>
public sealed record PayoutRefundInput(
    long RefundId,
    long BookingId,
    decimal RefundedAmount,
    decimal CommissionRateApplied);

public sealed record PayoutItemResult(
    long BookingId,
    long PaymentTransactionId,
    decimal PaidAmount,
    decimal DiscountAmount,
    decimal CommissionRateApplied,
    decimal CommissionAmount);

public sealed record PayoutRefundItemResult(
    long RefundId,
    long BookingId,
    decimal RefundedAmount,
    decimal CommissionRateApplied,
    decimal CommissionReduction);

public sealed record OperatorPayoutCalculationResult(
    decimal PaymentsReceived,
    decimal DiscountsGiven,
    decimal RefundedAmount,
    decimal NetSales,
    decimal CommissionAmount,
    decimal PayableNetBeforeAdjustments,
    decimal AdjustmentsApplied,
    decimal PayableNetAmount,
    int BookingCount,
    int ParticipantCount,
    IReadOnlyList<PayoutItemResult> Items,
    IReadOnlyList<PayoutRefundItemResult> RefundItems,
    IReadOnlyList<long> FaultyBookingIds);

/// <summary>
/// The single commission/net calculation shared by UC-44 (reporting) and UC-46 (settlement),
/// per the approved UC-44 BE spec formula (UC-46 BE spec v3, "Calculation"):
///   NetSales = PaymentsReceived − RefundedAmount
///   CommissionAmount = Σ per-payment commission − Σ per-refund commission reduction
///   PayableNet = NetSales − CommissionAmount − AdjustmentsApplied
///
/// UC-44 calls this with its reporting booking set (Confirmed + Completed) and no outstanding
/// adjustments. UC-46 calls this with its settlement booking set (Completed, past the payout
/// eligibility hold) and the operator's outstanding adjustments. The function itself does not
/// know or care which caller it is; only the inputs differ.
///
/// This type performs no database access and mutates nothing: it is pure arithmetic over the
/// rows the caller already loaded, so it is trivially unit-testable and guarantees the two
/// callers can never silently diverge.
/// </summary>
public static class OperatorPayoutCalculator
{
    public static OperatorPayoutCalculationResult Calculate(
        IReadOnlyList<PayoutPaymentInput> payments,
        IReadOnlyList<PayoutRefundInput> refunds,
        IReadOnlyList<decimal> outstandingAdjustments)
    {
        ArgumentNullException.ThrowIfNull(payments);
        ArgumentNullException.ThrowIfNull(refunds);
        ArgumentNullException.ThrowIfNull(outstandingAdjustments);

        var refundsByBooking = refunds
            .GroupBy(refund => refund.BookingId)
            .ToDictionary(group => group.Key, group => group.ToList());

        var faultyBookingIds = new List<long>();
        var items = new List<PayoutItemResult>(payments.Count);
        var refundItems = new List<PayoutRefundItemResult>(refunds.Count);

        decimal paymentsReceived = 0m;
        decimal discountsGiven = 0m;
        decimal refundedAmount = 0m;
        decimal commissionAmount = 0m;
        var participantCount = 0;

        foreach (var payment in payments)
        {
            paymentsReceived += payment.PaidAmount;
            discountsGiven += payment.DiscountAmount;
            participantCount += payment.ParticipantCount;

            var bookingRefunds = refundsByBooking.GetValueOrDefault(payment.BookingId, []);
            var bookingRefundTotal = bookingRefunds.Sum(refund => refund.RefundedAmount);

            // Refund bound (UC-46 BE spec rule 6): a booking's refunds must never exceed its
            // payment. A violation is a data fault, never silently clamped or ignored, so the
            // caller can surface it instead of recording a wrong payout.
            if (bookingRefundTotal > payment.PaidAmount)
            {
                faultyBookingIds.Add(payment.BookingId);
            }

            var paymentCommission = Math.Round(payment.PaidAmount * payment.CommissionRateApplied / 100m, 2);
            var commissionReductionTotal = 0m;

            foreach (var refund in bookingRefunds)
            {
                var reduction = Math.Round(refund.RefundedAmount * refund.CommissionRateApplied / 100m, 2);
                commissionReductionTotal += reduction;
                refundedAmount += refund.RefundedAmount;
                refundItems.Add(new PayoutRefundItemResult(
                    refund.RefundId, refund.BookingId, refund.RefundedAmount, refund.CommissionRateApplied, reduction));
            }

            var netPaymentCommission = Math.Max(0m, paymentCommission - commissionReductionTotal);
            commissionAmount += netPaymentCommission;

            items.Add(new PayoutItemResult(
                payment.BookingId, payment.PaymentTransactionId, payment.PaidAmount,
                payment.DiscountAmount, payment.CommissionRateApplied, netPaymentCommission));
        }

        var netSales = paymentsReceived - refundedAmount;
        var payableBeforeAdjustments = netSales - commissionAmount;

        decimal adjustmentsApplied = 0m;
        var remainingPayable = payableBeforeAdjustments;
        foreach (var adjustment in outstandingAdjustments)
        {
            if (remainingPayable <= 0m) break;
            // Adjustment amounts are stored negative; apply up to what's left to absorb,
            // never pushing the payable amount below zero (any excess stays outstanding).
            var absAdjustment = -adjustment;
            var applied = Math.Min(absAdjustment, remainingPayable);
            adjustmentsApplied += applied;
            remainingPayable -= applied;
        }

        var payableNet = payableBeforeAdjustments - adjustmentsApplied;

        return new OperatorPayoutCalculationResult(
            PaymentsReceived: paymentsReceived,
            DiscountsGiven: discountsGiven,
            RefundedAmount: refundedAmount,
            NetSales: netSales,
            CommissionAmount: commissionAmount,
            PayableNetBeforeAdjustments: payableBeforeAdjustments,
            AdjustmentsApplied: adjustmentsApplied,
            PayableNetAmount: payableNet,
            BookingCount: payments.Count,
            ParticipantCount: participantCount,
            Items: items,
            RefundItems: refundItems,
            FaultyBookingIds: faultyBookingIds);
    }
}