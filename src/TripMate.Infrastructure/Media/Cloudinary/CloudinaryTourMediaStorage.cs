using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using TripMate.Application.Common.Media;

namespace TripMate.Infrastructure.Media.Cloudinary;

internal sealed class CloudinaryTourMediaStorage(
    ICloudinaryClient client,
    IOptions<CloudinaryOptions> options,
    ILogger<CloudinaryTourMediaStorage> logger)
    : ITourMediaStorage
{
    private const string InvalidResponseErrorCode = "TOUR_MEDIA_STORAGE_INVALID_RESPONSE";
    private const string UnavailableErrorCode = "TOUR_MEDIA_STORAGE_UNAVAILABLE";
    private const string RejectedErrorCode = "TOUR_MEDIA_STORAGE_REJECTED";
    private readonly CloudinaryOptions options = options.Value;

    public string AllocatePublicId(long tourId)
    {
        if (tourId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(tourId));
        }

        return $"{options.TourMediaFolderRoot}/{tourId}/{Guid.NewGuid():N}";
    }

    public async Task<TourMediaStorageUploadResult> UploadAsync(
        TourMediaStorageUpload request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidatePublicId(request.PublicId);
        if (request.Bytes.Length == 0)
        {
            throw new ArgumentException("Sanitized image content is required.", nameof(request));
        }

        if (request.ContentType is not ("image/jpeg" or "image/png" or "image/webp"))
        {
            throw new ArgumentException("The sanitized image content type is unsupported.", nameof(request));
        }

        try
        {
            CloudinaryUploadResponse response = await client.UploadAsync(
                new CloudinaryUploadRequest(
                    request.PublicId,
                    request.ContentType,
                    request.Bytes,
                    Overwrite: false,
                    UseFilename: false,
                    UniqueFilename: false,
                    DiscardOriginalFilename: true),
                cancellationToken);

            if (response.Outcome == CloudinaryUploadOutcome.Succeeded)
            {
                if (!string.Equals(response.PublicId, request.PublicId, StringComparison.Ordinal) ||
                    response.SecureUrl is null ||
                    !string.Equals(
                        response.SecureUrl.Scheme,
                        Uri.UriSchemeHttps,
                        StringComparison.OrdinalIgnoreCase))
                {
                    logger.LogWarning(
                        "Tour media storage returned an invalid success response.");
                    return TourMediaStorageUploadResult.Failed(
                        TourMediaStorageFailureKind.ProviderRejected,
                        InvalidResponseErrorCode);
                }

                return TourMediaStorageUploadResult.Succeeded(response.SecureUrl);
            }

            return TourMediaStorageUploadResult.Failed(
                response.Outcome == CloudinaryUploadOutcome.TransientFailure
                    ? TourMediaStorageFailureKind.Transient
                    : TourMediaStorageFailureKind.ProviderRejected,
                response.SafeErrorCode ??
                    (response.Outcome == CloudinaryUploadOutcome.TransientFailure
                        ? UnavailableErrorCode
                        : RejectedErrorCode));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            logger.LogWarning("Tour media storage upload failed unexpectedly.");
            return TourMediaStorageUploadResult.Failed(
                TourMediaStorageFailureKind.Transient,
                UnavailableErrorCode);
        }
    }

    public async Task<TourMediaStorageDeleteResult> DestroyAsync(
        string publicId,
        CancellationToken cancellationToken)
    {
        ValidatePublicId(publicId);

        try
        {
            CloudinaryDeleteOutcome outcome = await client.DestroyAsync(
                new CloudinaryDeleteRequest(publicId, Invalidate: true),
                cancellationToken);

            return outcome switch
            {
                CloudinaryDeleteOutcome.Deleted => new(
                    TourMediaStorageDeleteOutcome.Deleted,
                    null),
                CloudinaryDeleteOutcome.AlreadyAbsent => new(
                    TourMediaStorageDeleteOutcome.AlreadyAbsent,
                    null),
                CloudinaryDeleteOutcome.TransientFailure => new(
                    TourMediaStorageDeleteOutcome.TransientFailure,
                    UnavailableErrorCode),
                _ => new(
                    TourMediaStorageDeleteOutcome.PermanentFailure,
                    RejectedErrorCode),
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            logger.LogWarning("Tour media storage deletion failed unexpectedly.");
            return new TourMediaStorageDeleteResult(
                TourMediaStorageDeleteOutcome.TransientFailure,
                UnavailableErrorCode);
        }
    }

    private void ValidatePublicId(string publicId)
    {
        if (string.IsNullOrWhiteSpace(publicId) ||
            !publicId.StartsWith(
                $"{options.TourMediaFolderRoot}/",
                StringComparison.Ordinal))
        {
            throw new ArgumentException("A valid allocated Tour media public identifier is required.", nameof(publicId));
        }
    }
}