using TripMate.Domain.Common;

namespace TripMate.Domain.Entities;

/// <summary>
/// Maps payment.PayoutSettlement (DAILY-PAYOUT-TRANSFER spec PD-3, PD-5). One row per booking
/// (<see cref="PayoutSettlementKind.Main"/>) or per retained part of a partially refunded
/// booking (<see cref="PayoutSettlementKind.Retained"/>). Its amount is fixed at creation and is
/// never recalculated afterwards (BR-146) — only <see cref="PayoutSettlementAdjustment"/> moves
/// money between settlements. Its status is driven by the <see cref="PayoutBatch"/> that carries
/// its transfer (PD-14); callers go through the batch, not this entity, to change status.
/// </summary>
public class PayoutSettlement : BaseEntity
{
    private PayoutSettlement()
    {
    }

    public long BatchId { get; private set; }

    public long BookingId { get; private set; }

    public long TourOperatorUserId { get; private set; }

    public DateOnly RunDate { get; private set; }

    public string Kind { get; private set; } = PayoutSettlementKind.Main;

    public decimal GrossAmount { get; private set; }

    public decimal CommissionRate { get; private set; }

    public decimal CommissionAmount { get; private set; }

    public decimal NetAmount { get; private set; }

    public decimal DebtOffsetAmount { get; private set; }

    public decimal PayoutAmount { get; private set; }

    public string Status { get; private set; } = PayoutSettlementStatus.Scheduled;

    public string? TransferReference { get; private set; }

    public DateTimeOffset CreatedAtUtc { get; private set; }

    public DateTimeOffset UpdatedAtUtc { get; private set; }

    public static PayoutSettlement Create(
        long batchId,
        long bookingId,
        long tourOperatorUserId,
        DateOnly runDate,
        string kind,
        decimal grossAmount,
        decimal commissionRate,
        decimal commissionAmount,
        decimal netAmount,
        decimal debtOffsetAmount,
        decimal payoutAmount,
        DateTimeOffset createdAtUtc)
    {
        if (!PayoutSettlementKind.All.Contains(kind))
            throw new ArgumentOutOfRangeException(nameof(kind));
        if (payoutAmount < 0)
            throw new ArgumentOutOfRangeException(nameof(payoutAmount), "A settlement's payout amount cannot be negative (PD-3).");
        if (commissionAmount < 0 || commissionAmount > grossAmount)
            throw new ArgumentOutOfRangeException(nameof(commissionAmount));
        if (netAmount != grossAmount - commissionAmount)
            throw new ArgumentException("Net amount must equal gross amount less commission.", nameof(netAmount));

        return new PayoutSettlement
        {
            BatchId = batchId,
            BookingId = bookingId,
            TourOperatorUserId = tourOperatorUserId,
            RunDate = runDate,
            Kind = kind,
            GrossAmount = grossAmount,
            CommissionRate = commissionRate,
            CommissionAmount = commissionAmount,
            NetAmount = netAmount,
            DebtOffsetAmount = debtOffsetAmount,
            PayoutAmount = payoutAmount,
            Status = PayoutSettlementStatus.Scheduled,
            CreatedAtUtc = createdAtUtc,
            UpdatedAtUtc = createdAtUtc,
        };
    }

    /// <summary>Applies a status reached by the owning <see cref="PayoutBatch"/>. Internal: only
    /// the batch (same assembly) may change a settlement's status, so the two never drift apart.</summary>
    internal void ApplyStatus(string status, string? transferReference, DateTimeOffset nowUtc)
    {
        Status = status;
        if (transferReference is not null)
            TransferReference = transferReference;
        UpdatedAtUtc = nowUtc;
    }

    /// <summary>Marks this settlement Carried Over on its own (PD-10): its amount is moved, via a
    /// <see cref="PayoutSettlementAdjustment"/>, into the batch of the NEXT run for the same
    /// operator — a different, later <see cref="PayoutBatch"/> record, so the batch-cascade in
    /// <see cref="PayoutBatch.MarkCarriedOver"/> does not apply here.</summary>
    public void MarkCarriedOverStandalone(DateTimeOffset nowUtc)
    {
        Status = PayoutSettlementStatus.CarriedOver;
        UpdatedAtUtc = nowUtc;
    }
}
