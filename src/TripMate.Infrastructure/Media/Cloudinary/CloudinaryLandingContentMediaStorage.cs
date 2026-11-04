using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using TripMate.Application.Common.Media;

namespace TripMate.Infrastructure.Media.Cloudinary;

/// <summary>Dedicated Cloudinary namespace for UC-66 images; never mixes assets with a Tour.</summary>
internal sealed class CloudinaryLandingContentMediaStorage(
    ICloudinaryClient client,
    IOptions<CloudinaryOptions> options,
    ILogger<CloudinaryLandingContentMediaStorage> logger) : ILandingContentMediaStorage
{
    private readonly string root = options.Value.TourMediaFolderRoot + "/landing-content";

    public string AllocatePublicId() => $"{root}/{Guid.NewGuid():N}";

    public async Task<LandingContentMediaUploadResult> UploadAsync(LandingContentMediaUpload upload, CancellationToken cancellationToken)
    {
        try
        {
            var result = await client.UploadAsync(new CloudinaryUploadRequest(upload.PublicId, upload.ContentType,
                upload.Bytes, false, false, false, true), cancellationToken);
            if (result.Outcome == CloudinaryUploadOutcome.Succeeded && result.SecureUrl is { Scheme: "https" } url &&
                result.PublicId == upload.PublicId)
                return new LandingContentMediaUploadResult(url, null);
            return new LandingContentMediaUploadResult(null, result.SafeErrorCode ?? "LANDING_CONTENT_MEDIA_REJECTED");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception)
        {
            logger.LogWarning("Landing-content media upload failed.");
            return new LandingContentMediaUploadResult(null, "LANDING_CONTENT_MEDIA_UNAVAILABLE");
        }
    }
}