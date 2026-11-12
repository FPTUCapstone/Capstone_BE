using TripMate.Domain.Common;
using TripMate.Domain.Enums;

namespace TripMate.Domain.Entities;

public class Refund : BaseEntity
{
    public const int ReasonMaxLength = 500;
    public const int GatewayReferenceMaxLength = 150;
    public const int ErrorCodeMaxLength = 100;
    public const int ErrorMessageMaxLength = 1000;
    public const int ReturnReferenceMaxLength = 150;

    private Refund()
    {
    }

    public long BookingId { get; private set; }
    public Booking Booking { get; private set; } = null!;
    public long? TransactionId { get; private set; }
    public long? CancellationOperationId { get; private set; }
    public decimal Amount { get; private set; }
    public string? Reason { get; private set; }
    public RefundInitiator InitiatedBy { get; private set; }
    public RefundStatus Status { get; private set; }
    public DateTimeOffset RequestedAtUtc { get; private set; }
    public DateTimeOffset? ProcessedAtUtc { get; private set; }
    public string? GatewayReference { get; private set; }
    public int AttemptCount { get; private set; }
    public DateTimeOffset? LastAttemptAtUtc { get; private set; }
    public DateTimeOffset? NextAttemptAtUtc { get; private set; }
    public string? LastErrorCode { get; private set; }
    public string? LastErrorMessage { get; private set; }

    // UC-42 (SRS 3.8.4.3, BR-139, BR-144): set only while the refund is going through the
    // operator-confirmation / partner-return path (PendingOperatorConfirmation onward).
    public decimal? OperatorReturnShare { get; private set; }
    public DateTimeOffset? OperatorConfirmedAtUtc { get; private set; }
    public DateTimeOffset? ReturnDeadlineAtUtc { get; private set; }
    public string? ReturnReference { get; private set; }
    public DateTimeOffset? ReturnReportedAtUtc { get; private set; }
    public DateTimeOffset? ReturnReceivedAtUtc { get; private set; }

    /// <summary>
    /// UC-41 creates a refund in this status when a verified payment exists and its amount
    /// matches the booking's paid amount (3.8.4.2 step 6, BR-143) — the refund is not yet
    /// eligible for the automatic gateway sweep (<see cref="TryStartAttempt"/>); it only becomes
    /// so via <see cref="Confirm"/> (directly, when the operator's share is zero) or via
    /// <see cref="RecordReturnReceived"/> after the operator's share has been returned.
    /// </summary>
    public static Refund CreatePendingOperatorConfirmation(long bookingId, long transactionId,
        long cancellationOperationId, decimal amount, string? reason, RefundInitiator initiatedBy,
        DateTimeOffset requestedAtUtc)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(bookingId);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(transactionId);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(cancellationOperationId);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(amount);
        if (reason?.Length > ReasonMaxLength)
            throw new ArgumentOutOfRangeException(nameof(reason));

        return new Refund
        {
            BookingId = bookingId,
            TransactionId = transactionId,
            CancellationOperationId = cancellationOperationId,
            Amount = amount,
            Reason = reason,
            InitiatedBy = initiatedBy,
            Status = RefundStatus.PendingOperatorConfirmation,
            RequestedAtUtc = requestedAtUtc.ToUniversalTime(),
        };
    }

    /// <summary>
    /// UC-41 6.b1: the verified transaction's amount does not match the booking's paid amount —
    /// created directly in this terminal review status (never goes through operator confirmation
    /// or the automatic gateway sweep) for an Administrator to resolve.
    /// </summary>
    public static Refund CreateReconciliation(long bookingId, long transactionId, long cancellationOperationId,
        decimal amount, string? reason, RefundInitiator initiatedBy, DateTimeOffset requestedAtUtc)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(bookingId);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(transactionId);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(cancellationOperationId);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(amount);
        if (reason?.Length > ReasonMaxLength)
            throw new ArgumentOutOfRangeException(nameof(reason));

        return new Refund
        {
            BookingId = bookingId,
            TransactionId = transactionId,
            CancellationOperationId = cancellationOperationId,
            Amount = amount,
            Reason = reason,
            InitiatedBy = initiatedBy,
            Status = RefundStatus.Reconciliation,
            RequestedAtUtc = requestedAtUtc.ToUniversalTime(),
        };
    }

    /// <summary>
    /// UC-42 step 3/3.a1 (SRS 3.8.4.3): the Tour Operator confirms the refund (or the system
    /// auto-confirms it after 3 working days unanswered, BR-187 — the caller is responsible for
    /// that distinction; this method's effect on the refund's own state is identical either way).
    /// A zero <paramref name="operatorReturnShare"/> (BR-139: no settlement transferred yet) skips
    /// straight to <see cref="RefundStatus.Pending"/>, ready for the automatic gateway sweep
    /// (abnormal case 4.b1); any other share moves to <see cref="RefundStatus.AwaitingPartnerReturn"/>
    /// with the given return deadline.
    /// </summary>
    public void Confirm(decimal operatorReturnShare, DateTimeOffset? returnDeadlineAtUtc, DateTimeOffset nowUtc)
    {
        if (Status != RefundStatus.PendingOperatorConfirmation)
            throw new InvalidOperationException($"Cannot confirm a refund in status {Status}.");
        if (operatorReturnShare < 0m)
            throw new ArgumentOutOfRangeException(nameof(operatorReturnShare), "The operator's return share cannot be negative.");
        if (operatorReturnShare > 0m && returnDeadlineAtUtc is null)
            throw new ArgumentException("A non-zero operator return share requires a return deadline.", nameof(returnDeadlineAtUtc));

        var now = nowUtc.ToUniversalTime();
        OperatorReturnShare = operatorReturnShare;
        OperatorConfirmedAtUtc = now;

        if (operatorReturnShare == 0m)
        {
            Status = RefundStatus.Pending;
            NextAttemptAtUtc = now;
        }
        else
        {
            Status = RefundStatus.AwaitingPartnerReturn;
            ReturnDeadlineAtUtc = returnDeadlineAtUtc!.Value.ToUniversalTime();
        }
    }

    /// <summary>UC-42 step 5 (SRS 3.8.4.3): the Tour Operator reports having transferred its
    /// share by bank transfer, with the transfer reference to reconcile against (MSG172 if
    /// missing).</summary>
    public void ReportReturn(string returnReference, DateTimeOffset nowUtc)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(returnReference);
        if (returnReference.Trim().Length > ReturnReferenceMaxLength)
            throw new ArgumentOutOfRangeException(nameof(returnReference));
        if (Status != RefundStatus.AwaitingPartnerReturn)
            throw new InvalidOperationException($"Cannot report a return for a refund in status {Status}.");

        Status = RefundStatus.ReturnReported;
        ReturnReference = returnReference.Trim();
        ReturnReportedAtUtc = nowUtc.ToUniversalTime();
    }

    /// <summary>UC-42 step 7-8 (SRS 3.8.4.3): a Staff member has reconciled the TripMate bank
    /// statement and confirms the full operator share has been credited — the refund becomes
    /// eligible for the automatic gateway sweep (UC-67, BR-143), with the amount fixed at
    /// creation (never recalculated).</summary>
    public void RecordReturnReceived(DateTimeOffset nowUtc)
    {
        if (Status != RefundStatus.ReturnReported)
            throw new InvalidOperationException($"Cannot record a return received for a refund in status {Status}.");

        var now = nowUtc.ToUniversalTime();
        Status = RefundStatus.Pending;
        ReturnReceivedAtUtc = now;
        NextAttemptAtUtc = now;
    }

    /// <summary>BR-161: the configured number of gateway retries has been exhausted — the refund
    /// is published to the Escalated Refunds Monitor for an Administrator to resolve. The amount
    /// is never recalculated.</summary>
    public void Escalate(string errorCode, string? errorMessage)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(errorCode);
        if (errorCode.Trim().Length > ErrorCodeMaxLength || errorMessage?.Trim().Length > ErrorMessageMaxLength)
            throw new ArgumentOutOfRangeException(nameof(errorCode));
        if (Status != RefundStatus.Processing)
            throw new InvalidOperationException("Only an in-progress refund can be escalated.");

        Status = RefundStatus.Escalated;
        LastErrorCode = errorCode.Trim();
        LastErrorMessage = string.IsNullOrWhiteSpace(errorMessage) ? null : errorMessage.Trim();
        NextAttemptAtUtc = null;
    }

    public static Refund CreatePending(long bookingId, long transactionId, long cancellationOperationId,
        decimal amount, string? reason, RefundInitiator initiatedBy, DateTimeOffset requestedAtUtc)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(bookingId);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(transactionId);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(cancellationOperationId);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(amount);
        if (reason?.Length > ReasonMaxLength)
            throw new ArgumentOutOfRangeException(nameof(reason));

        return new Refund
        {
            BookingId = bookingId,
            TransactionId = transactionId,
            CancellationOperationId = cancellationOperationId,
            Amount = amount,
            Reason = reason,
            InitiatedBy = initiatedBy,
            Status = RefundStatus.Pending,
            RequestedAtUtc = requestedAtUtc.ToUniversalTime(),
            NextAttemptAtUtc = requestedAtUtc.ToUniversalTime(),
        };
    }

    /// <summary>
    /// Reserves a due automatic refund attempt. A refund can never be submitted manually by a
    /// traveler or operator; UC-42 owns the gateway interaction after UC-41 has fixed the amount.
    /// </summary>
    public bool TryStartAttempt(DateTimeOffset nowUtc)
    {
        var now = nowUtc.ToUniversalTime();
        if (Status is not (RefundStatus.Pending or RefundStatus.RetryPending) ||
            (NextAttemptAtUtc is DateTimeOffset dueAt && dueAt > now))
        {
            return false;
        }

        Status = RefundStatus.Processing;
        AttemptCount++;
        LastAttemptAtUtc = now;
        NextAttemptAtUtc = null;
        LastErrorCode = null;
        LastErrorMessage = null;
        return true;
    }

    public void MarkProcessed(string gatewayReference, DateTimeOffset nowUtc)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gatewayReference);
        if (gatewayReference.Trim().Length > GatewayReferenceMaxLength)
            throw new ArgumentOutOfRangeException(nameof(gatewayReference));
        if (Status is not (RefundStatus.Processing or RefundStatus.ManualReview))
            throw new InvalidOperationException("Only an in-progress or manually reviewed refund can be completed.");

        Status = RefundStatus.Processed;
        GatewayReference = gatewayReference.Trim();
        ProcessedAtUtc = nowUtc.ToUniversalTime();
        NextAttemptAtUtc = null;
    }

    public void ScheduleRetry(string errorCode, string? errorMessage, DateTimeOffset nextAttemptAtUtc)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(errorCode);
        if (errorCode.Trim().Length > ErrorCodeMaxLength || errorMessage?.Trim().Length > ErrorMessageMaxLength)
            throw new ArgumentOutOfRangeException(nameof(errorCode));
        if (Status != RefundStatus.Processing)
            throw new InvalidOperationException("Only an in-progress refund can be retried.");

        Status = RefundStatus.RetryPending;
        LastErrorCode = errorCode.Trim();
        LastErrorMessage = string.IsNullOrWhiteSpace(errorMessage) ? null : errorMessage.Trim();
        NextAttemptAtUtc = nextAttemptAtUtc.ToUniversalTime();
    }

    /// <summary>
    /// Stops automatic processing without claiming that money was returned. This is used when
    /// the original provider requires an operator-mediated refund rather than exposing a safe
    /// original-channel refund API.
    /// </summary>
    public void RequireManualReview(string reasonCode, string? reasonMessage)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reasonCode);
        if (reasonCode.Trim().Length > ErrorCodeMaxLength || reasonMessage?.Trim().Length > ErrorMessageMaxLength)
            throw new ArgumentOutOfRangeException(nameof(reasonCode));
        if (Status != RefundStatus.Processing)
            throw new InvalidOperationException("Only an in-progress refund can require manual review.");

        Status = RefundStatus.ManualReview;
        LastErrorCode = reasonCode.Trim();
        LastErrorMessage = string.IsNullOrWhiteSpace(reasonMessage) ? null : reasonMessage.Trim();
        NextAttemptAtUtc = null;
    }

    /// <summary>
    /// Releases an abandoned gateway submission after its processing lease. The gateway request
    /// always carries a stable refund-derived idempotency key, so recovery cannot create another
    /// refund at an idempotency-capable provider.
    /// </summary>
    public bool RecoverExpiredAttempt(DateTimeOffset nowUtc, TimeSpan leaseDuration, int maxAttempts,
        TimeSpan retryDelay)
    {
        if (leaseDuration <= TimeSpan.Zero || maxAttempts <= 0 || retryDelay <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(leaseDuration));

        var now = nowUtc.ToUniversalTime();
        if (Status != RefundStatus.Processing || LastAttemptAtUtc is not DateTimeOffset attemptedAt ||
            attemptedAt.Add(leaseDuration) > now)
        {
            return false;
        }

        const string code = "payment.refund.processing_lease_expired";
        const string message = "The gateway submission did not complete before its processing lease expired.";
        if (AttemptCount >= maxAttempts)
            MarkFailed(code, message);
        else
            ScheduleRetry(code, message, now.Add(retryDelay));
        return true;
    }

    public void Reject(string errorCode, string? errorMessage)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(errorCode);
        if (errorCode.Trim().Length > ErrorCodeMaxLength || errorMessage?.Trim().Length > ErrorMessageMaxLength)
            throw new ArgumentOutOfRangeException(nameof(errorCode));
        if (Status is RefundStatus.Processed or RefundStatus.Rejected)
            throw new InvalidOperationException("A terminal refund cannot be rejected.");

        Status = RefundStatus.Rejected;
        LastErrorCode = errorCode.Trim();
        LastErrorMessage = string.IsNullOrWhiteSpace(errorMessage) ? null : errorMessage.Trim();
        NextAttemptAtUtc = null;
    }

    public void MarkFailed(string errorCode, string? errorMessage)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(errorCode);
        if (errorCode.Trim().Length > ErrorCodeMaxLength || errorMessage?.Trim().Length > ErrorMessageMaxLength)
            throw new ArgumentOutOfRangeException(nameof(errorCode));
        if (Status != RefundStatus.Processing)
            throw new InvalidOperationException("Only an in-progress refund can fail.");

        Status = RefundStatus.Failed;
        LastErrorCode = errorCode.Trim();
        LastErrorMessage = string.IsNullOrWhiteSpace(errorMessage) ? null : errorMessage.Trim();
        NextAttemptAtUtc = null;
    }
}