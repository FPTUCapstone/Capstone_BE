namespace TripMate.Application.Common.Media;

public sealed record OperatorDocumentStorageUpload(
    string PublicId,
    string ContentType,
    byte[] Bytes);

public sealed record OperatorDocumentStorageUploadResult(
    Uri? DeliveryUrl,
    TourMediaStorageFailureKind? FailureKind,
    string? SafeErrorCode)
{
    public bool IsSuccess => DeliveryUrl is not null;

    public static OperatorDocumentStorageUploadResult Succeeded(Uri deliveryUrl)
    {
        ArgumentNullException.ThrowIfNull(deliveryUrl);
        return new OperatorDocumentStorageUploadResult(deliveryUrl, null, null);
    }

    public static OperatorDocumentStorageUploadResult Failed(
        TourMediaStorageFailureKind failureKind,
        string safeErrorCode) =>
        new(null, failureKind, safeErrorCode);
}

public enum OperatorDocumentStorageDeleteOutcome
{
    Deleted = 1,
    AlreadyAbsent = 2,
    TransientFailure = 3,
    PermanentFailure = 4,
}

public sealed record OperatorDocumentStorageDeleteResult(
    OperatorDocumentStorageDeleteOutcome Outcome,
    string? SafeErrorCode);

/// <summary>
/// Stores Tour Operator application documents submitted through UC-02 and returns the
/// delivery URL persisted into dbo.OperatorDocuments.file_url.
/// </summary>
public interface IOperatorDocumentStorage
{
    /// <summary>Allocates a provider-safe public id for one document upload.</summary>
    string AllocatePublicId();

    Task<OperatorDocumentStorageUploadResult> UploadAsync(
        OperatorDocumentStorageUpload request,
        CancellationToken cancellationToken);

    Task<OperatorDocumentStorageDeleteResult> DeleteAsync(
        string publicId,
        string contentType,
        CancellationToken cancellationToken);
}