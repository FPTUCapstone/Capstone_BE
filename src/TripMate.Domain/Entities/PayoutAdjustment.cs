namespace TripMate.Domain.Entities;

/// <summary>
/// UC-46: a refund that completed too late to be netted against the Payout its booking belongs
/// to (because that Payout's snapshot was already taken, or the booking's hold period had
/// already passed without a payout yet existing). It becomes an outstanding negative adjustment
/// against the operator and is applied — fully or partially — to a future payout request. It
/// never retroactively changes an already-submitted snapshot (see UC-46 BE spec rule 8).
/// </summary>
public sealed class PayoutAdjustment
{
    private PayoutAdjustment()
    {
    }

    public long Id { get; private set; }

    public long OperatorUserId { get; private set; }

    public long SourceRefundId { get; private set; }

    /// <summary>Always negative or zero once fully applied; never positive.</summary>
    public decimal Amount { get; private set; }

    public DateTimeOffset CreatedAtUtc { get; private set; }

    public long? AppliedPayoutId { get; private set; }

    public decimal? AppliedAmount { get; private set; }

    public DateTimeOffset? AppliedAtUtc { get; private set; }

    public bool IsOutstanding => AppliedPayoutId is null;

    public static PayoutAdjustment Create(long operatorUserId, long sourceRefundId, decimal amount, DateTimeOffset createdAtUtc)
    {
        if (operatorUserId <= 0) throw new ArgumentOutOfRangeException(nameof(operatorUserId));
        if (sourceRefundId <= 0) throw new ArgumentOutOfRangeException(nameof(sourceRefundId));
        if (amount >= 0) throw new ArgumentOutOfRangeException(nameof(amount), "A payout adjustment must be a negative amount.");

        return new PayoutAdjustment
        {
            OperatorUserId = operatorUserId,
            SourceRefundId = sourceRefundId,
            Amount = amount,
            CreatedAtUtc = createdAtUtc,
        };
    }

    /// <summary>Applies this adjustment to a payout. Partial application leaves the remainder
    /// outstanding as a new adjustment row (the caller creates that row); this instance becomes
    /// fully applied for the portion consumed.</summary>
    public void Apply(long payoutId, decimal appliedAmount, DateTimeOffset appliedAtUtc)
    {
        if (!IsOutstanding) throw new InvalidOperationException("Adjustment is already applied.");
        if (appliedAmount >= 0) throw new ArgumentOutOfRangeException(nameof(appliedAmount), "Applied amount must be negative.");
        if (appliedAmount < Amount) throw new ArgumentOutOfRangeException(nameof(appliedAmount), "Cannot apply more than the outstanding amount.");

        AppliedPayoutId = payoutId;
        AppliedAmount = appliedAmount;
        AppliedAtUtc = appliedAtUtc;
    }

    /// <summary>Returns this adjustment to outstanding (payout Cancelled or Rejected).</summary>
    public void Revert()
    {
        AppliedPayoutId = null;
        AppliedAmount = null;
        AppliedAtUtc = null;
    }
}