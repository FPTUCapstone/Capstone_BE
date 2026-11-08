using TripMate.Domain.Common;

namespace TripMate.Domain.Entities;

/// <summary>
/// Maps payment.PayoutSettlementAdjustment (DAILY-PAYOUT-TRANSFER spec PD-10, PD-5). Records a
/// carried-over amount from a <see cref="PayoutSettlement"/> whose batch failed, to be added to
/// the next <see cref="PayoutBatch"/> created for the same Tour Operator. Named distinctly from
/// the pre-existing <see cref="PayoutAdjustment"/> (post-trip-refund branch, a different ledger)
/// to avoid a naming collision between the two features.
/// </summary>
public class PayoutSettlementAdjustment : BaseEntity
{
    public const int ReasonMaxLength = 200;

    private PayoutSettlementAdjustment()
    {
    }

    public long SourceSettlementId { get; private set; }

    public long TourOperatorUserId { get; private set; }

    public decimal Amount { get; private set; }

    public string Reason { get; private set; } = string.Empty;

    public DateTimeOffset CreatedAtUtc { get; private set; }

    public long? AppliedBatchId { get; private set; }

    public DateTimeOffset? AppliedAtUtc { get; private set; }

    public bool IsOutstanding => AppliedBatchId is null;

    public static PayoutSettlementAdjustment CreateCarryOver(
        long sourceSettlementId, long tourOperatorUserId, decimal amount, DateTimeOffset createdAtUtc)
    {
        if (amount <= 0)
            throw new ArgumentOutOfRangeException(nameof(amount));

        return new PayoutSettlementAdjustment
        {
            SourceSettlementId = sourceSettlementId,
            TourOperatorUserId = tourOperatorUserId,
            Amount = amount,
            Reason = "CarriedOver",
            CreatedAtUtc = createdAtUtc,
        };
    }

    public void Apply(long batchId, DateTimeOffset appliedAtUtc)
    {
        if (AppliedBatchId is not null)
            throw new InvalidOperationException("This adjustment has already been applied.");

        AppliedBatchId = batchId;
        AppliedAtUtc = appliedAtUtc;
    }
}
