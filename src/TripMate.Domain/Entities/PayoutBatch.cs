using TripMate.Domain.Common;

namespace TripMate.Domain.Entities;

/// <summary>
/// Maps payment.PayoutBatch (DAILY-PAYOUT-TRANSFER spec PD-14). One gathered transfer order per
/// Tour Operator per daily run — the unit the gateway actually moves money as, so TripMate sends
/// one bank transfer per operator per day instead of one per booking. Its <see cref="Settlements"/>
/// are the per-booking accounting lines (PD-5); every status transition on the batch cascades to
/// them (PD-10), so a settlement's status never has to be set on its own except when it is
/// carried over standalone (<see cref="PayoutSettlement.MarkCarriedOverStandalone"/>).
///
/// Every transition method here is a precondition guard, like <see cref="Refund"/>'s: the caller
/// (Application layer) is expected to check <see cref="Status"/> before invoking an action that
/// does not apply, and gets an <see cref="InvalidOperationException"/> if it does not.
/// </summary>
public class PayoutBatch : BaseEntity
{
    private readonly List<PayoutSettlement> _settlements = [];

    private PayoutBatch()
    {
    }

    public DateOnly RunDate { get; private set; }

    public long TourOperatorUserId { get; private set; }

    public decimal TotalAmount { get; private set; }

    public string Status { get; private set; } = PayoutSettlementStatus.Scheduled;

    public string? TransferReference { get; private set; }

    public int AttemptCount { get; private set; }

    public DateTimeOffset? LastAttemptAtUtc { get; private set; }

    public DateTimeOffset CreatedAtUtc { get; private set; }

    public DateTimeOffset UpdatedAtUtc { get; private set; }

    public IReadOnlyCollection<PayoutSettlement> Settlements => _settlements;

    public static PayoutBatch Create(DateOnly runDate, long tourOperatorUserId, DateTimeOffset createdAtUtc) => new()
    {
        RunDate = runDate,
        TourOperatorUserId = tourOperatorUserId,
        TotalAmount = 0m,
        Status = PayoutSettlementStatus.Scheduled,
        CreatedAtUtc = createdAtUtc,
        UpdatedAtUtc = createdAtUtc,
    };

    /// <summary>Adds a settlement created for this batch's operator/run and folds its payout
    /// amount into the batch total (PD-14).</summary>
    public void AddSettlement(PayoutSettlement settlement)
    {
        _settlements.Add(settlement);
        TotalAmount += settlement.PayoutAmount;
    }

    /// <summary>Folds a carried-over amount (PD-10) from a past failed batch into this one. No
    /// new settlement line is created — the original settlement that generated this amount
    /// already exists and stays Carried Over; only the money moves, through this batch's single
    /// transfer, into the operator's account.</summary>
    public void IncludeCarriedOverAmount(decimal amount)
    {
        if (amount <= 0)
            throw new ArgumentOutOfRangeException(nameof(amount));
        if (Status != PayoutSettlementStatus.Scheduled)
            throw new InvalidOperationException("Carried-over amounts can only be added before the batch starts processing.");

        TotalAmount += amount;
    }

    public void MarkProcessing(DateTimeOffset nowUtc)
    {
        if (Status is not (PayoutSettlementStatus.Scheduled or PayoutSettlementStatus.SettlementFailed))
            throw new InvalidOperationException($"Cannot start processing a batch in status {Status}.");

        Status = PayoutSettlementStatus.Processing;
        AttemptCount += 1;
        LastAttemptAtUtc = nowUtc;
        UpdatedAtUtc = nowUtc;
        Cascade(nowUtc, null);
    }

    /// <summary>Records one more gateway attempt while the batch stays Processing (PD-7): the
    /// PD-7a lookup came back still-unresolved, so the sweep resent it. Distinct from
    /// <see cref="MarkProcessing"/>, which is the one-time transition INTO Processing.</summary>
    public void RecordRetryAttempt(DateTimeOffset nowUtc)
    {
        if (Status != PayoutSettlementStatus.Processing)
            throw new InvalidOperationException($"Cannot record a retry attempt on a batch in status {Status}.");

        AttemptCount += 1;
        LastAttemptAtUtc = nowUtc;
        UpdatedAtUtc = nowUtc;
    }

    public void MarkSettled(string transferReference, DateTimeOffset nowUtc)
    {
        if (Status != PayoutSettlementStatus.Processing)
            throw new InvalidOperationException($"Cannot settle a batch in status {Status}.");
        ArgumentException.ThrowIfNullOrWhiteSpace(transferReference);

        Status = PayoutSettlementStatus.Settled;
        TransferReference = transferReference;
        UpdatedAtUtc = nowUtc;
        Cascade(nowUtc, transferReference);
    }

    public void MarkFailed(DateTimeOffset nowUtc)
    {
        if (Status != PayoutSettlementStatus.Processing)
            throw new InvalidOperationException($"Cannot fail a batch in status {Status}.");

        Status = PayoutSettlementStatus.SettlementFailed;
        UpdatedAtUtc = nowUtc;
        Cascade(nowUtc, null);
    }

    /// <summary>Settles a batch whose total is zero (every settlement in it was fully absorbed
    /// by the operator's pending debt, PD-15) without ever calling the gateway — there is
    /// nothing to transfer. Only valid while the batch is still Scheduled.</summary>
    public void MarkSettledWithoutTransfer(DateTimeOffset nowUtc)
    {
        if (Status != PayoutSettlementStatus.Scheduled)
            throw new InvalidOperationException($"Cannot settle-without-transfer a batch in status {Status}.");
        if (TotalAmount != 0m)
            throw new InvalidOperationException("Only a zero-total batch can be settled without a transfer.");

        Status = PayoutSettlementStatus.Settled;
        UpdatedAtUtc = nowUtc;
        Cascade(nowUtc, null);
    }

    /// <summary>Administrator retries a failed batch (UC-73, PD-10). The amount is never
    /// recalculated; this only moves the batch back to Processing so the gateway is called
    /// again — the caller is still responsible for the PD-7a lookup before resending.</summary>
    public void Retry(DateTimeOffset nowUtc)
    {
        if (Status != PayoutSettlementStatus.SettlementFailed)
            throw new InvalidOperationException($"Cannot retry a batch in status {Status}.");

        Status = PayoutSettlementStatus.Processing;
        AttemptCount += 1;
        LastAttemptAtUtc = nowUtc;
        UpdatedAtUtc = nowUtc;
        Cascade(nowUtc, null);
    }

    /// <summary>Administrator records a transfer made outside the system for a failed batch
    /// (PD-10). Requires a reference, same as the automatic path.</summary>
    public void MarkSettledManually(string transferReference, DateTimeOffset nowUtc)
    {
        if (Status != PayoutSettlementStatus.SettlementFailed)
            throw new InvalidOperationException($"Cannot manually settle a batch in status {Status}.");
        ArgumentException.ThrowIfNullOrWhiteSpace(transferReference);

        Status = PayoutSettlementStatus.Settled;
        TransferReference = transferReference;
        UpdatedAtUtc = nowUtc;
        Cascade(nowUtc, transferReference);
    }

    /// <summary>Administrator carries a failed batch's total over to the next run of the same
    /// operator (PD-10). This batch and its settlements keep their own amount and move to
    /// Carried Over (PC-02 of the SRS) — the carried amount is recorded separately as one
    /// <see cref="PayoutSettlementAdjustment"/> per settlement by the caller.</summary>
    public void MarkCarriedOver(DateTimeOffset nowUtc)
    {
        if (Status != PayoutSettlementStatus.SettlementFailed)
            throw new InvalidOperationException($"Cannot carry over a batch in status {Status}.");

        Status = PayoutSettlementStatus.CarriedOver;
        UpdatedAtUtc = nowUtc;
        Cascade(nowUtc, null);
    }

    private void Cascade(DateTimeOffset nowUtc, string? transferReference)
    {
        foreach (var settlement in _settlements)
            settlement.ApplyStatus(Status, transferReference, nowUtc);
    }
}