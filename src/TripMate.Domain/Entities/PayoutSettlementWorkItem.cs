namespace TripMate.Domain.Entities;

/// <summary>
/// Durable work item created atomically with the UC-65 authorization. The payout-settlement
/// worker owns it from then on; there is at most one per payout.
/// </summary>
public sealed class PayoutSettlementWorkItem
{
    public const int StateMaxLength = 24;
    public const int GatewayReferenceMaxLength = 200;
    public const int LastErrorCodeMaxLength = 100;

    /// <summary>Error code recorded when a crashed worker's lease is reclaimed. A submission that
    /// follows it must first look the payout up at the gateway before sending again.</summary>
    public const string LeaseExpiredCode = "lease_expired";

    public const string StatePending = "Pending";
    public const string StateSubmitting = "Submitting";
    public const string StateSubmitted = "Submitted";
    public const string StateCompleted = "Completed";
    public const string StateManualReconciliation = "ManualReconciliation";

    public static readonly string[] AllStates =
        [
            StatePending,
            StateSubmitting,
            StateSubmitted,
            StateCompleted,
            StateManualReconciliation,
        ];

    private PayoutSettlementWorkItem()
    {
    }

    public long Id { get; private set; }

    public long PayoutId { get; private set; }

    public string State { get; private set; } = StatePending;

    /// <summary>Number of gateway submit attempts made so far.</summary>
    public int AttemptCount { get; private set; }

    /// <summary>When the worker may next act on this item; null once it is terminal.</summary>
    public DateTimeOffset? NextAttemptAtUtc { get; private set; }

    public Guid? LeaseToken { get; private set; }

    public DateTimeOffset? LeaseExpiresAtUtc { get; private set; }

    /// <summary>Provider payout identifier returned when the gateway accepted the transfer.</summary>
    public string? GatewayReference { get; private set; }

    public DateTimeOffset? SubmittedAtUtc { get; private set; }

    /// <summary>Polling stops after this instant; the payout is then left for manual reconciliation.</summary>
    public DateTimeOffset? PollDeadlineAtUtc { get; private set; }

    public int PollCount { get; private set; }

    public DateTimeOffset? LastPolledAtUtc { get; private set; }

    public string? LastErrorCode { get; private set; }

    public DateTimeOffset CreatedAtUtc { get; private set; }

    public DateTimeOffset? CompletedAtUtc { get; private set; }

    public byte[] RowVersion { get; private set; } = [];

    public static PayoutSettlementWorkItem Create(long payoutId, DateTimeOffset nowUtc)
    {
        if (payoutId <= 0)
            throw new ArgumentOutOfRangeException(nameof(payoutId));

        var now = nowUtc.ToUniversalTime();
        return new PayoutSettlementWorkItem
        {
            PayoutId = payoutId,
            State = StatePending,
            NextAttemptAtUtc = now,
            CreatedAtUtc = now,
        };
    }

    /// <summary>
    /// Takes the item for one unit of work under a lease. A due Pending item starts a submit
    /// attempt (state Submitting, attempt counted); a due Submitted item is taken for a poll and
    /// keeps its state. Refused while another lease is held, before the due time, and in terminal states.
    /// </summary>
    public bool TryClaim(DateTimeOffset nowUtc, Guid leaseToken, TimeSpan leaseDuration)
    {
        if (leaseToken == Guid.Empty)
            throw new ArgumentException("A lease token is required.", nameof(leaseToken));
        if (leaseDuration <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(leaseDuration));

        var now = nowUtc.ToUniversalTime();
        if (LeaseToken is not null || NextAttemptAtUtc is not DateTimeOffset dueAt || dueAt > now)
            return false;

        switch (State)
        {
            case StatePending:
                State = StateSubmitting;
                AttemptCount++;
                break;
            case StateSubmitted:
                break;
            default:
                return false;
        }

        LeaseToken = leaseToken;
        LeaseExpiresAtUtc = now + leaseDuration;
        NextAttemptAtUtc = null;
        return true;
    }

    public bool RecordSubmitted(
        Guid leaseToken,
        string gatewayReference,
        DateTimeOffset nowUtc,
        TimeSpan pollWindow,
        DateTimeOffset firstPollAtUtc)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gatewayReference);
        var reference = gatewayReference.Trim();
        if (reference.Length > GatewayReferenceMaxLength)
            throw new ArgumentException("Gateway reference is too long.", nameof(gatewayReference));
        if (pollWindow <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(pollWindow));

        if (!HoldsLease(StateSubmitting, leaseToken))
            return false;

        var now = nowUtc.ToUniversalTime();
        State = StateSubmitted;
        GatewayReference = reference;
        SubmittedAtUtc = now;
        PollDeadlineAtUtc = now + pollWindow;
        NextAttemptAtUtc = firstPollAtUtc.ToUniversalTime();
        LastErrorCode = null;
        ReleaseLease();
        return true;
    }

    public bool ScheduleSubmitRetry(Guid leaseToken, string errorCode, DateTimeOffset nextAttemptAtUtc)
    {
        var code = NormalizeErrorCode(errorCode);
        if (!HoldsLease(StateSubmitting, leaseToken))
            return false;

        State = StatePending;
        NextAttemptAtUtc = nextAttemptAtUtc.ToUniversalTime();
        LastErrorCode = code;
        ReleaseLease();
        return true;
    }

    public bool RecordPoll(Guid leaseToken, DateTimeOffset polledAtUtc, DateTimeOffset nextPollAtUtc)
    {
        if (!HoldsLease(StateSubmitted, leaseToken))
            return false;

        PollCount++;
        LastPolledAtUtc = polledAtUtc.ToUniversalTime();
        NextAttemptAtUtc = nextPollAtUtc.ToUniversalTime();
        ReleaseLease();
        return true;
    }

    public bool IsPastPollDeadline(DateTimeOffset nowUtc) =>
        PollDeadlineAtUtc is DateTimeOffset deadline && nowUtc.ToUniversalTime() > deadline;

    /// <summary>The payout reached its final outcome (Settled or SettlementFailed).</summary>
    public bool Complete(Guid leaseToken, DateTimeOffset completedAtUtc)
    {
        if (!HoldsLease(StateSubmitting, leaseToken) && !HoldsLease(StateSubmitted, leaseToken))
            return false;

        State = StateCompleted;
        CompletedAtUtc = completedAtUtc.ToUniversalTime();
        NextAttemptAtUtc = null;
        ReleaseLease();
        return true;
    }

    /// <summary>The worker can no longer decide the outcome. The payout stays SettlementProcessing
    /// and a person reconciles it; no further gateway call is made for this item.</summary>
    public bool RequireReconciliation(Guid leaseToken, string errorCode)
    {
        var code = NormalizeErrorCode(errorCode);
        if (!HoldsLease(StateSubmitting, leaseToken) && !HoldsLease(StateSubmitted, leaseToken))
            return false;

        State = StateManualReconciliation;
        LastErrorCode = code;
        NextAttemptAtUtc = null;
        ReleaseLease();
        return true;
    }

    /// <summary>
    /// Reclaims a lease whose holder never reported back. A crashed submit returns to Pending (the
    /// next attempt looks the payout up before sending again) or, with attempts exhausted, goes to
    /// manual reconciliation; a crashed poll simply becomes due again.
    /// </summary>
    public bool RecoverExpiredLease(DateTimeOffset nowUtc, int maxSubmitAttempts)
    {
        var now = nowUtc.ToUniversalTime();
        if (LeaseToken is null || LeaseExpiresAtUtc is not DateTimeOffset expiresAt || expiresAt > now)
            return false;

        switch (State)
        {
            case StateSubmitting when AttemptCount >= maxSubmitAttempts:
                State = StateManualReconciliation;
                LastErrorCode = LeaseExpiredCode;
                NextAttemptAtUtc = null;
                break;
            case StateSubmitting:
                State = StatePending;
                LastErrorCode = LeaseExpiredCode;
                NextAttemptAtUtc = now;
                break;
            case StateSubmitted:
                NextAttemptAtUtc = now;
                break;
            default:
                return false;
        }

        ReleaseLease();
        return true;
    }

    private bool HoldsLease(string expectedState, Guid leaseToken) =>
        State == expectedState && LeaseToken is Guid held && held == leaseToken && leaseToken != Guid.Empty;

    private void ReleaseLease()
    {
        LeaseToken = null;
        LeaseExpiresAtUtc = null;
    }

    private static string NormalizeErrorCode(string errorCode)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(errorCode);
        var code = errorCode.Trim();
        if (code.Length > LastErrorCodeMaxLength)
            throw new ArgumentException("Error code is too long.", nameof(errorCode));

        return code;
    }
}