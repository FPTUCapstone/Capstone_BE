namespace TripMate.Domain.Entities;

/// <summary>
/// One settlement month folded into a Payout under the "available balance" model (UC-46, decided
/// 2026-10-23): a single payout request can claim several outstanding closed months at once, and
/// this row keeps that month's own figures for audit — so "which months funded this payout, and
/// how much did each contribute" is never lost even though the Payout header only stores totals.
/// </summary>
public sealed class PayoutPeriodBreakdown
{
    private PayoutPeriodBreakdown()
    {
    }

    public long Id { get; private set; }

    public long PayoutId { get; private set; }

    /// <summary>First day of the Vietnam-time settlement month this row covers.</summary>
    public DateOnly SettlementMonth { get; private set; }

    public int EligibleBookingCount { get; private set; }

    public decimal PaymentsReceived { get; private set; }

    public decimal DiscountsGiven { get; private set; }

    public decimal RefundedAmount { get; private set; }

    public decimal CommissionAmount { get; private set; }

    public decimal PayableNetAmount { get; private set; }

    public static PayoutPeriodBreakdown Create(
        DateOnly settlementMonth,
        int eligibleBookingCount,
        decimal paymentsReceived,
        decimal discountsGiven,
        decimal refundedAmount,
        decimal commissionAmount,
        decimal payableNetAmount)
    {
        if (eligibleBookingCount < 0) throw new ArgumentOutOfRangeException(nameof(eligibleBookingCount));
        if (paymentsReceived < 0 || discountsGiven < 0 || refundedAmount < 0 || commissionAmount < 0)
            throw new ArgumentOutOfRangeException(nameof(paymentsReceived));

        return new PayoutPeriodBreakdown
        {
            SettlementMonth = new DateOnly(settlementMonth.Year, settlementMonth.Month, 1),
            EligibleBookingCount = eligibleBookingCount,
            PaymentsReceived = paymentsReceived,
            DiscountsGiven = discountsGiven,
            RefundedAmount = refundedAmount,
            CommissionAmount = commissionAmount,
            PayableNetAmount = payableNetAmount,
        };
    }
}