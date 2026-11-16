using TripMate.Domain.Common;

namespace TripMate.Domain.Entities;

public sealed class PostTripComplaintEvidenceAmendmentOperation : BaseEntity
{
    public const int PayloadFingerprintLength = 64;
    private PostTripComplaintEvidenceAmendmentOperation() { }
    public long TravelerUserId { get; private set; }
    public Guid IdempotencyKey { get; private set; }
    public string PayloadFingerprint { get; private set; } = string.Empty;
    public long ComplaintId { get; private set; }
    public DateTimeOffset CreatedAtUtc { get; private set; }

    public static PostTripComplaintEvidenceAmendmentOperation Create(long travelerUserId,
        Guid idempotencyKey, string payloadFingerprint, long complaintId, DateTimeOffset createdAtUtc)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(travelerUserId);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(complaintId);
        ArgumentException.ThrowIfNullOrWhiteSpace(payloadFingerprint);
        if (payloadFingerprint.Length != PayloadFingerprintLength) throw new ArgumentOutOfRangeException(nameof(payloadFingerprint));
        return new PostTripComplaintEvidenceAmendmentOperation
        {
            TravelerUserId = travelerUserId,
            IdempotencyKey = idempotencyKey,
            PayloadFingerprint = payloadFingerprint,
            ComplaintId = complaintId,
            CreatedAtUtc = createdAtUtc.ToUniversalTime(),
        };
    }
}