using TripMate.Domain.Common;
using TripMate.Domain.Enums;

namespace TripMate.Domain.Entities;

public class Refund : BaseEntity
{
    public const int ReasonMaxLength = 500;
    public const int GatewayReferenceMaxLength = 150;
    public const int ErrorCodeMaxLength = 100;
    public const int ErrorMessageMaxLength = 1000;

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