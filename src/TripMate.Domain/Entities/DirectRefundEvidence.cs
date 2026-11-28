using TripMate.Domain.Common;

namespace TripMate.Domain.Entities;

/// <summary>
/// Maps payment.DirectRefundEvidences. Private file uploaded by a TO as proof of transfer.
/// Retained for 24 months after case closure; disputed cases never auto-deleted.
/// </summary>
public sealed class DirectRefundEvidence : BaseEntity
{
    public const int OriginalFileNameMaxLength = 255;
    public const int ContentTypeMaxLength = 100;
    public const int StorageObjectKeyMaxLength = 500;
    public const int ChecksumMaxLength = 128;
    public const long MaxFileSizeBytes = 5 * 1024 * 1024; // 5 MB
    public const int MinFilesPerReport = 1;
    public const int MaxFilesPerReport = 3;

    private static readonly HashSet<string> AllowedContentTypes =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "application/pdf",
            "image/jpeg",
            "image/jpg",
            "image/png",
        };

    private DirectRefundEvidence()
    {
    }

    public long CaseId { get; private set; }

    public string OriginalFileName { get; private set; } = string.Empty;

    public string ContentType { get; private set; } = string.Empty;

    public long ByteLength { get; private set; }

    public string StorageObjectKey { get; private set; } = string.Empty;

    public string Checksum { get; private set; } = string.Empty;

    public DateTimeOffset UploadedAt { get; private set; }

    public static bool IsAllowedContentType(string contentType) =>
        AllowedContentTypes.Contains(contentType);

    public static DirectRefundEvidence Create(
        long caseId,
        string originalFileName,
        string contentType,
        long byteLength,
        string storageObjectKey,
        string checksum,
        DateTimeOffset uploadedAt)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(caseId);
        ArgumentException.ThrowIfNullOrWhiteSpace(originalFileName);
        ArgumentException.ThrowIfNullOrWhiteSpace(contentType);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(byteLength);
        ArgumentException.ThrowIfNullOrWhiteSpace(storageObjectKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(checksum);

        if (!IsAllowedContentType(contentType))
            throw new ArgumentException($"Content type '{contentType}' is not allowed.", nameof(contentType));
        if (byteLength > MaxFileSizeBytes)
            throw new ArgumentOutOfRangeException(nameof(byteLength), $"File size {byteLength} exceeds maximum {MaxFileSizeBytes}.");

        return new DirectRefundEvidence
        {
            CaseId = caseId,
            OriginalFileName = originalFileName,
            ContentType = contentType,
            ByteLength = byteLength,
            StorageObjectKey = storageObjectKey,
            Checksum = checksum,
            UploadedAt = uploadedAt.ToUniversalTime(),
        };
    }
}