using TripMate.Domain.Common;

namespace TripMate.Domain.Entities;

public sealed class ComplaintEvidence : BaseEntity
{
    public const int OriginalFileNameMaxLength = 255;
    public const int ContentTypeMaxLength = 100;
    public const int StorageObjectKeyMaxLength = 500;
    public const int ChecksumMaxLength = 128;
    private ComplaintEvidence() { }
    public long ComplaintId { get; private set; }
    public string OriginalFileName { get; private set; } = string.Empty;
    public string ContentType { get; private set; } = string.Empty;
    public long ByteLength { get; private set; }
    public string StorageObjectKey { get; private set; } = string.Empty;
    public string Checksum { get; private set; } = string.Empty;
    public DateTimeOffset CreatedAtUtc { get; private set; }
    public long? StaffReviewEventId { get; private set; }
    public ComplaintStaffReviewEvent? StaffReviewEvent { get; private set; }

    public void AssociateStaffReview(ComplaintStaffReviewEvent review)
    {
        if (review.ComplaintId != ComplaintId || StaffReviewEvent is not null)
            throw new InvalidOperationException("Evidence must belong to its original review event.");
        StaffReviewEvent = review;
    }

    public static ComplaintEvidence Create(long complaintId, string originalFileName, string contentType,
        long byteLength, string storageObjectKey, string checksum, DateTimeOffset createdAtUtc)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(complaintId);
        ArgumentException.ThrowIfNullOrWhiteSpace(originalFileName);
        ArgumentException.ThrowIfNullOrWhiteSpace(contentType);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(byteLength);
        ArgumentException.ThrowIfNullOrWhiteSpace(storageObjectKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(checksum);
        string name = originalFileName.Trim(), type = contentType.Trim(), key = storageObjectKey.Trim(), hash = checksum.Trim();
        if (name.Length > OriginalFileNameMaxLength || type.Length > ContentTypeMaxLength || key.Length > StorageObjectKeyMaxLength || hash.Length > ChecksumMaxLength)
            throw new ArgumentOutOfRangeException(nameof(originalFileName));
        return new ComplaintEvidence { ComplaintId = complaintId, OriginalFileName = name, ContentType = type, ByteLength = byteLength, StorageObjectKey = key, Checksum = hash, CreatedAtUtc = createdAtUtc.ToUniversalTime() };
    }
}