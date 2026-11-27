using Microsoft.Extensions.Logging;

using TripMate.Application.Common.Media;

namespace TripMate.Infrastructure.Media.Cloudinary;

/// <summary>Public Cloudinary storage for the Tour Operator logo (UC-34, BR-16). The logo is shown to
/// Travelers, so it is a normal deliverable image, not a signed private document.</summary>
internal sealed class CloudinaryOperatorLogoStorage(
    ICloudinaryClient client,
    ILogger<CloudinaryOperatorLogoStorage> logger) : IOperatorLogoStorage
{
    internal const string Root = "tripmate/operator-logos";
    internal const string UnavailableCode = "OPERATOR_LOGO_STORAGE_UNAVAILABLE";
    private static readonly string[] Types = ["image/jpeg", "image/png", "image/webp"];

    public string AllocatePublicId(long operatorUserId)
    {
        if (operatorUserId <= 0) throw new ArgumentOutOfRangeException(nameof(operatorUserId));
        return $"{Root}/{operatorUserId}/{Guid.NewGuid():N}";
    }

    public async Task<OperatorLogoUploadResult> UploadAsync(
        OperatorLogoUpload upload, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(upload.PublicId)
            || !upload.PublicId.StartsWith($"{Root}/", StringComparison.Ordinal)
            || upload.Bytes.Length == 0
            || !Types.Contains(upload.ContentType, StringComparer.OrdinalIgnoreCase))
        {
            throw new ArgumentException("The operator logo upload is invalid.", nameof(upload));
        }

        try
        {
            CloudinaryUploadResponse response = await client.UploadAsync(new CloudinaryUploadRequest(
                upload.PublicId, upload.ContentType, upload.Bytes, Overwrite: false,
                UseFilename: false, UniqueFilename: false, DiscardOriginalFilename: true), cancellationToken);
            if (response.Outcome != CloudinaryUploadOutcome.Succeeded
                || response.SecureUrl is null
                || !response.SecureUrl.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(response.PublicId, upload.PublicId, StringComparison.Ordinal))
            {
                return OperatorLogoUploadResult.Failed(UnavailableCode);
            }

            return OperatorLogoUploadResult.Succeeded(response.SecureUrl);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Operator logo upload failed.");
            return OperatorLogoUploadResult.Failed(UnavailableCode);
        }
    }

    public async Task<bool> DeleteAsync(string publicId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(publicId) || !publicId.StartsWith($"{Root}/", StringComparison.Ordinal))
        {
            throw new ArgumentException("The operator logo public id is invalid.", nameof(publicId));
        }

        try
        {
            CloudinaryDeleteOutcome outcome = await client.DestroyAsync(
                new CloudinaryDeleteRequest(publicId, Invalidate: true), cancellationToken);
            return outcome is CloudinaryDeleteOutcome.Deleted or CloudinaryDeleteOutcome.AlreadyAbsent;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Operator logo cleanup failed.");
            return false;
        }
    }
}