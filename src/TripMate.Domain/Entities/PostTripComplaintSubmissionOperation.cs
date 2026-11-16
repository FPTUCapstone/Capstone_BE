namespace TripMate.Domain.Entities;

/// <summary>Durable idempotency record for a UC-68 complaint submission.</summary>
public sealed class PostTripComplaintSubmissionOperation
{
    public const int PayloadFingerprintLength = 64;

    private PostTripComplaintSubmissionOperation() { }

    public long Id { get; private set; }
    public long TravelerUserId { get; private set; }
    public Guid IdempotencyKey { get; private set; }
    public string PayloadFingerprint { get; private set; } = string.Empty;
    public long ComplaintId { get; private set; }
    public PostTripComplaint? Complaint { get; private set; }
    public DateTimeOffset CreatedAtUtc { get; private set; }

    public static PostTripComplaintSubmissionOperation Create(long travelerUserId, Guid idempotencyKey,
        string payloadFingerprint, PostTripComplaint complaint, DateTimeOffset createdAtUtc)
    {
        ArgumentNullException.ThrowIfNull(complaint);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(travelerUserId);
        if (idempotencyKey == Guid.Empty) throw new ArgumentOutOfRangeException(nameof(idempotencyKey));
        if (string.IsNullOrWhiteSpace(payloadFingerprint) || payloadFingerprint.Length != PayloadFingerprintLength)
            throw new ArgumentException("A SHA-256 payload fingerprint is required.", nameof(payloadFingerprint));

        return new PostTripComplaintSubmissionOperation
        {
            TravelerUserId = travelerUserId,
            IdempotencyKey = idempotencyKey,
            PayloadFingerprint = payloadFingerprint,
            Complaint = complaint,
            CreatedAtUtc = createdAtUtc.ToUniversalTime(),
        };
    }
}