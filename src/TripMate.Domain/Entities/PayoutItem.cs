namespace TripMate.Domain.Entities;

/// <summary>One contributing successful payment captured into a Payout snapshot (UC-46).</summary>
public sealed class PayoutItem
{
    private PayoutItem()
    {
    }

    public long Id { get; private set; }

    public long PayoutId { get; private set; }

    public long BookingId { get; private set; }

    public long PaymentTransactionId { get; private set; }

    public decimal PaidAmount { get; private set; }

    public decimal DiscountAmount { get; private set; }

    public decimal CommissionRateApplied { get; private set; }

    public decimal CommissionAmount { get; private set; }

    public static PayoutItem Create(
        long bookingId,
        long paymentTransactionId,
        decimal paidAmount,
        decimal discountAmount,
        decimal commissionRateApplied,
        decimal commissionAmount)
    {
        if (bookingId <= 0) throw new ArgumentOutOfRangeException(nameof(bookingId));
        if (paymentTransactionId <= 0) throw new ArgumentOutOfRangeException(nameof(paymentTransactionId));
        if (paidAmount < 0) throw new ArgumentOutOfRangeException(nameof(paidAmount));
        if (discountAmount < 0) throw new ArgumentOutOfRangeException(nameof(discountAmount));
        if (commissionAmount < 0) throw new ArgumentOutOfRangeException(nameof(commissionAmount));

        return new PayoutItem
        {
            BookingId = bookingId,
            PaymentTransactionId = paymentTransactionId,
            PaidAmount = paidAmount,
            DiscountAmount = discountAmount,
            CommissionRateApplied = commissionRateApplied,
            CommissionAmount = commissionAmount,
        };
    }
}