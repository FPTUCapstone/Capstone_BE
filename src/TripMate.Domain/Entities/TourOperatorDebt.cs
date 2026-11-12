using TripMate.Domain.Common;

namespace TripMate.Domain.Entities;

/// <summary>
/// Maps payment.TourOperatorDebt (DAILY-PAYOUT-TRANSFER spec v6, PD-15, BR-144, BR-188). An
/// amount a Tour Operator owes TripMate, written by the post-trip-refund branch (out of scope
/// here) and read by the daily payout run to offset it against the operator's next settlements
/// (PD-15) and by the auto-lock check (BR-189) to decide whether 15 days have passed with debt
/// still unrecovered. <see cref="RecoveredAmount"/> never exceeds <see cref="Amount"/>.
/// </summary>
public class TourOperatorDebt : BaseEntity
{
    private TourOperatorDebt()
    {
    }

    public long TourOperatorUserId { get; private set; }

    public decimal Amount { get; private set; }

    public decimal RecoveredAmount { get; private set; }

    public DateTimeOffset IncurredAtUtc { get; private set; }

    public DateTimeOffset CreatedAtUtc { get; private set; }

    /// <summary>The part of <see cref="Amount"/> not yet recovered — what the daily run can still
    /// offset against (PD-15) and what BR-189's 15-day check looks at.</summary>
    public decimal OutstandingAmount => Amount - RecoveredAmount;

    public static TourOperatorDebt Create(
        long tourOperatorUserId, decimal amount, DateTimeOffset incurredAtUtc, DateTimeOffset createdAtUtc)
    {
        if (amount <= 0)
            throw new ArgumentOutOfRangeException(nameof(amount));

        return new TourOperatorDebt
        {
            TourOperatorUserId = tourOperatorUserId,
            Amount = amount,
            RecoveredAmount = 0m,
            IncurredAtUtc = incurredAtUtc,
            CreatedAtUtc = createdAtUtc,
        };
    }

    /// <summary>Records that a daily run offset <paramref name="amount"/> of this debt against a
    /// settlement's payout (PD-15).</summary>
    public void Recover(decimal amount)
    {
        if (amount <= 0)
            throw new ArgumentOutOfRangeException(nameof(amount));
        if (amount > OutstandingAmount)
            throw new ArgumentOutOfRangeException(nameof(amount), "Cannot recover more than the outstanding amount.");

        RecoveredAmount += amount;
    }
}