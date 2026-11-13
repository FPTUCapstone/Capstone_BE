namespace TripMate.Application.Features.Bookings.Refunds;

/// <summary>
/// Pure calculator for BR-139 (SRS 3.8.4.3, spec v1.2): the Tour Operator's return share of a
/// refund. Takes no dependency on the database — the caller supplies the refund amount, the
/// amount already transferred to the Tour Operator for the booking (the sum of its Settled
/// payment.PayoutSettlement rows — 0 before the Advance settlement, 70% of PaidAmount after it,
/// 90% after the Final settlement, per DAILY-PAYOUT-TRANSFER), and the booking's own PaidAmount.
/// </summary>
public static class RefundShareCalculator
{
    /// <summary>Rounded down to the whole VND unit, like every other money calculation in this
    /// codebase (BR-146/PD-3) — VND has no subdivision. TripMate pays the remainder of the refund
    /// from the amount it retains; this is only the operator's own share.</summary>
    public static decimal CalculateOperatorReturnShare(decimal refundAmount, decimal transferredToOperatorAmount,
        decimal paidAmount)
    {
        if (refundAmount < 0m)
            throw new ArgumentOutOfRangeException(nameof(refundAmount), "Refund amount cannot be negative.");
        if (transferredToOperatorAmount < 0m)
        {
            throw new ArgumentOutOfRangeException(nameof(transferredToOperatorAmount),
                "The amount already transferred to the Tour Operator cannot be negative.");
        }
        if (paidAmount <= 0m)
            throw new ArgumentOutOfRangeException(nameof(paidAmount), "Paid amount must be positive.");

        return Math.Floor(refundAmount * transferredToOperatorAmount / paidAmount);
    }
}