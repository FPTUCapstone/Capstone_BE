namespace TripMate.Domain.Entities;

/// <summary>One refund that reduced a Payout's net amount (UC-46). Mirrors the payment captured
/// in a PayoutItem: the commission rate here is the rate of the payment this refund reverses.</summary>
public sealed class PayoutRefundItem
{
    private PayoutRefundItem()
    {
    }

    public long Id { get; private set; }

    public long PayoutId { get; private set; }

    public long RefundId { get; private set; }

    public long BookingId { get; private set; }

    public decimal RefundedAmount { get; private set; }

    public decimal CommissionRateApplied { get; private set; }

    public decimal CommissionReduction { get; private set; }

    public static PayoutRefundItem Create(
        long refundId,
        long bookingId,
        decimal refundedAmount,
        decimal commissionRateApplied,
        decimal commissionReduction)
    {
        if (refundId <= 0) throw new ArgumentOutOfRangeException(nameof(refundId));
        if (bookingId <= 0) throw new ArgumentOutOfRangeException(nameof(bookingId));
        if (refundedAmount < 0) throw new ArgumentOutOfRangeException(nameof(refundedAmount));
        if (commissionReduction < 0) throw new ArgumentOutOfRangeException(nameof(commissionReduction));

        return new PayoutRefundItem
        {
            RefundId = refundId,
            BookingId = bookingId,
            RefundedAmount = refundedAmount,
            CommissionRateApplied = commissionRateApplied,
            CommissionReduction = commissionReduction,
        };
    }
}