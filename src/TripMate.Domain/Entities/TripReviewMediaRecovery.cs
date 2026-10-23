namespace TripMate.Domain.Entities;

public sealed class TripReviewMediaRecovery
{
    public const string NeverDispatched = "NeverDispatched", Unknown = "Unknown", Succeeded = "Succeeded", Rejected = "Rejected";
    public const string Transient = "Transient", Permanent = "Permanent", Configuration = "Configuration", OutcomeUnknown = "OutcomeUnknown";
    public Guid OperationId { get; private set; }
    public Guid? UploadFence { get; private set; }
    public DateTimeOffset? UploadLeaseUntilUtc { get; private set; }
    public string UploadOutcome { get; private set; } = NeverDispatched;
    public Guid? CleanupFence { get; private set; }
    public DateTimeOffset? CleanupLeaseUntilUtc { get; private set; }
    public int Attempts { get; private set; }
    public DateTimeOffset? NextAttemptAtUtc { get; private set; }
    public bool Exhausted { get; private set; }
    public string? LastFailureCode { get; private set; }
    public DateTimeOffset? LastFailureAtUtc { get; private set; }
    public byte[] Version { get; private set; } = [];
    public bool HasTerminalUploadEvidence => UploadOutcome is NeverDispatched or Succeeded or Rejected;
    private TripReviewMediaRecovery() { }
    public static TripReviewMediaRecovery Create(Guid operationId)
    {
        if (operationId == Guid.Empty) throw new ArgumentException("Operation identity is required.", nameof(operationId));
        return new() { OperationId = operationId };
    }
    public bool TryClaimUpload(Guid fence, DateTimeOffset now)
    {
        if (fence == Guid.Empty || UploadOutcome != NeverDispatched || CleanupFence != null || Attempts != 0 || Exhausted) return false;
        UploadFence = fence; UploadLeaseUntilUtc = now.ToUniversalTime().AddMinutes(2); UploadOutcome = Unknown; return true;
    }
    public bool TryObserveUpload(Guid fence, bool succeeded)
    {
        if (fence == Guid.Empty || UploadFence != fence) return false;
        var outcome = succeeded ? Succeeded : Rejected;
        if (UploadOutcome != Unknown) return UploadOutcome == outcome;
        UploadOutcome = outcome; return true;
    }
    public bool TryObserveProviderSuccess(Guid cleanupFence, DateTimeOffset now)
    {
        if (!OwnsCleanup(cleanupFence, now) || UploadOutcome == Rejected) return false;
        UploadOutcome = Succeeded; return true;
    }
    public bool TryClaimCleanup(Guid fence, DateTimeOffset now)
    {
        now = now.ToUniversalTime();
        if (fence == Guid.Empty || Exhausted || CleanupLeaseUntilUtc > now || (UploadOutcome == Unknown && UploadLeaseUntilUtc > now) || NextAttemptAtUtc > now) return false;
        if (Attempts >= 8) { Exhausted = true; NextAttemptAtUtc = null; return false; }
        Attempts++; CleanupFence = fence; CleanupLeaseUntilUtc = now.AddMinutes(2); NextAttemptAtUtc = null; return true;
    }
    public bool OwnsCleanup(Guid fence, DateTimeOffset now) => fence != Guid.Empty && CleanupFence == fence && CleanupLeaseUntilUtc > now && !Exhausted;
    public bool CompleteCleanup(Guid fence, DateTimeOffset now, bool providerResolved) => OwnsCleanup(fence, now) && HasTerminalUploadEvidence && providerResolved;
    public bool RecordFailure(Guid fence, DateTimeOffset now, string code, bool permanent = false)
    {
        if (!OwnsCleanup(fence, now) || code is not (Transient or Permanent or Configuration or OutcomeUnknown)) return false;
        LastFailureCode = code; LastFailureAtUtc = now.ToUniversalTime();
        Exhausted = permanent || Attempts >= 8;
        NextAttemptAtUtc = Exhausted ? null : now.ToUniversalTime().AddMinutes(Math.Min(1440, Math.Pow(2, Attempts - 1)));
        CleanupFence = null; CleanupLeaseUntilUtc = null; return true;
    }
}