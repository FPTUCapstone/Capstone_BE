namespace TripMate.Application.Common.Media;

public sealed record TourMediaStorageUpload(
    string PublicId,
    string ContentType,
    byte[] Bytes);

public enum TourMediaStorageFailureKind
{
    ProviderRejected = 1,
    Transient = 2,
}

public sealed record TourMediaStorageUploadResult(
    Uri? DeliveryUrl,
    TourMediaStorageFailureKind? FailureKind,
    string? SafeErrorCode)
{
    public bool IsSuccess => DeliveryUrl is not null;

    public static TourMediaStorageUploadResult Succeeded(Uri deliveryUrl)
    {
        ArgumentNullException.ThrowIfNull(deliveryUrl);
        return new TourMediaStorageUploadResult(deliveryUrl, null, null);
    }

    public static TourMediaStorageUploadResult Failed(
        TourMediaStorageFailureKind failureKind,
        string safeErrorCode) =>
        new(null, failureKind, safeErrorCode);
}

public enum TourMediaStorageDeleteOutcome
{
    Deleted = 1,
    AlreadyAbsent = 2,
    TransientFailure = 3,
    PermanentFailure = 4,
}

public sealed record TourMediaStorageDeleteResult(
    TourMediaStorageDeleteOutcome Outcome,
    string? SafeErrorCode);

public interface ITourMediaStorage
{
    string AllocatePublicId(long tourId);

    Task<TourMediaStorageUploadResult> UploadAsync(
        TourMediaStorageUpload request,
        CancellationToken cancellationToken);

    Task<TourMediaStorageDeleteResult> DestroyAsync(
        string publicId,
        CancellationToken cancellationToken);
}