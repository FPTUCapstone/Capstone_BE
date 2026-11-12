using TripMate.Domain.Common;

namespace TripMate.Domain.Entities;

/// <summary>
/// Maps payment.PayoutBatch (DAILY-PAYOUT-TRANSFER spec v6 PD-14). One gathered transfer order
/// per Tour Operator per daily run — the unit the gateway actually moves money as, so TripMate
/// sends one bank transfer per operator per day instead of one per booking. Its
/// <see cref="Settlements"/> are the per-booking accounting lines (PD-5); every status
/// transition on the batch cascades to them (PD-10).
///
/// Every transition method here is a precondition guard: the caller (Application layer) is
/// expected to check <see cref="Status"/> before invoking an action that does not apply, and
/// gets an <see cref="InvalidOperationException"/> if it does not.
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

    /// <summary>Settles a batch after a successful transfer. Valid from <c>Processing</c> (a
    /// send just succeeded), <c>Scheduled</c> or <c>SettlementFailed</c> (a PD-7a lookup revealed
    /// a past, unconfirmed send actually succeeded — no fresh attempt was made, so no attempt is
    /// consumed either; the latter covers UC-73 [Retry Transfer]'s own PD-7a check before it ever
    /// calls <see cref="Retry"/>).</summary>
    public void MarkSettled(string transferReference, DateTimeOffset nowUtc)
    {
        if (Status is not (PayoutSettlementStatus.Processing or PayoutSettlementStatus.Scheduled or PayoutSettlementStatus.SettlementFailed))
            throw new InvalidOperationException($"Cannot settle a batch in status {Status}.");
        ArgumentException.ThrowIfNullOrWhiteSpace(transferReference);

        Status = PayoutSettlementStatus.Settled;
        TransferReference = transferReference;
        UpdatedAtUtc = nowUtc;
        Cascade(nowUtc, transferReference);
    }

    /// <summary>Fails a batch after a send or a PD-7a lookup came back permanently rejected.
    /// Valid from <c>Processing</c> (a send just came back Rejected, or the retry budget was
    /// just exhausted), <c>Scheduled</c> or <c>SettlementFailed</c> (a lookup revealed a past send
    /// was Rejected — the latter covers UC-73 [Retry Transfer]'s own PD-7a check, confirming the
    /// batch's already-failed state without consuming a fresh attempt).</summary>
    public void MarkFailed(DateTimeOffset nowUtc)
    {
        if (Status is not (PayoutSettlementStatus.Processing or PayoutSettlementStatus.Scheduled or PayoutSettlementStatus.SettlementFailed))
            throw new InvalidOperationException($"Cannot fail a batch in status {Status}.");

        Status = PayoutSettlementStatus.SettlementFailed;
        UpdatedAtUtc = nowUtc;
        Cascade(nowUtc, null);
    }

    /// <summary>Fails a batch before any gateway attempt was ever made (SRS 3.10.5, 1.b1: the
    /// Tour Operator's beneficiary bank account is missing or incomplete, MSG146) — unlike
    /// <see cref="MarkFailed"/>, this does not consume an attempt, since no transfer was sent.</summary>
    public void MarkFailedWithoutAttempt(DateTimeOffset nowUtc)
    {
        if (Status != PayoutSettlementStatus.Scheduled)
            throw new InvalidOperationException($"Cannot fail-without-attempt a batch in status {Status}.");

        Status = PayoutSettlementStatus.SettlementFailed;
        UpdatedAtUtc = nowUtc;
        Cascade(nowUtc, null);
    }

    /// <summary>SRS 3.10.5, abnormal case 3.a1: a sent transfer came back Failed or unanswered
    /// and the retry budget (BR-145) is not yet exhausted — the batch rests as Scheduled until
    /// the next hourly attempt, rather than staying Processing between attempts. The attempt that
    /// just failed was already recorded by the preceding <see cref="MarkProcessing"/> call.</summary>
    public void ReturnToScheduledAfterFailedAttempt(DateTimeOffset nowUtc)
    {
        if (Status != PayoutSettlementStatus.Processing)
            throw new InvalidOperationException($"Cannot return to Scheduled a batch in status {Status}.");

        Status = PayoutSettlementStatus.Scheduled;
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

    /// <summary>Administrator closes a permanently failed batch without transferring money
    /// (UC-73 [Mark as Resolved], PD-10). <paramref name="reason"/> is required (MSG178) and is
    /// not stored on the batch itself — the caller records it on the audit log entry.</summary>
    public void MarkResolved(string reason, DateTimeOffset nowUtc)
    {
        if (Status != PayoutSettlementStatus.SettlementFailed)
            throw new InvalidOperationException($"Cannot resolve a batch in status {Status}.");
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);

        Status = PayoutSettlementStatus.Resolved;
        UpdatedAtUtc = nowUtc;
        Cascade(nowUtc, null);
    }

    private void Cascade(DateTimeOffset nowUtc, string? transferReference)
    {
        foreach (var settlement in _settlements)
            settlement.ApplyStatus(Status, transferReference, nowUtc);
    }
}