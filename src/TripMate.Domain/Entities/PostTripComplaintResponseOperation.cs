using TripMate.Domain.Common;

namespace TripMate.Domain.Entities;

/// <summary>Durable idempotency record for one UC-69 operator response.</summary>
public sealed class PostTripComplaintResponseOperation : BaseEntity
{
    public const int PayloadFingerprintLength = 64;

    private PostTripComplaintResponseOperation() { }

    public long OperatorUserId { get; private set; }
    public Guid IdempotencyKey { get; private set; }
    public string PayloadFingerprint { get; private set; } = string.Empty;
    public long ComplaintId { get; private set; }
    public DateTimeOffset CreatedAtUtc { get; private set; }

    public static PostTripComplaintResponseOperation Create(long operatorUserId, Guid idempotencyKey,
        string payloadFingerprint, long complaintId, DateTimeOffset createdAtUtc)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(operatorUserId);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(complaintId);
        if (idempotencyKey == Guid.Empty) throw new ArgumentOutOfRangeException(nameof(idempotencyKey));
        ArgumentException.ThrowIfNullOrWhiteSpace(payloadFingerprint);
        if (payloadFingerprint.Length != PayloadFingerprintLength)
            throw new ArgumentOutOfRangeException(nameof(payloadFingerprint));
        return new PostTripComplaintResponseOperation
        {
            OperatorUserId = operatorUserId,
            IdempotencyKey = idempotencyKey,
            PayloadFingerprint = payloadFingerprint,
            ComplaintId = complaintId,
            CreatedAtUtc = createdAtUtc.ToUniversalTime(),
        };
    }
}