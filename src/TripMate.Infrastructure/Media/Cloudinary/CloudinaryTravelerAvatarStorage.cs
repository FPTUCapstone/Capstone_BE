using Microsoft.Extensions.Logging;

using TripMate.Application.Common.Media;

namespace TripMate.Infrastructure.Media.Cloudinary;

internal sealed class CloudinaryTravelerAvatarStorage(
    ICloudinaryClient client,
    ILogger<CloudinaryTravelerAvatarStorage> logger) : ITravelerAvatarStorage
{
    private const string Root = "tripmate/avatars";
    private static readonly string[] Types = ["image/jpeg", "image/png", "image/webp"];

    public string AllocatePublicId(long userId)
    {
        if (userId <= 0) throw new ArgumentOutOfRangeException(nameof(userId));
        return $"{Root}/{userId}/{Guid.NewGuid():N}";
    }

    public async Task<TravelerAvatarUploadResult> UploadAsync(
        TravelerAvatarUpload upload, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(upload.PublicId) || !upload.PublicId.StartsWith($"{Root}/", StringComparison.Ordinal) ||
            upload.Bytes.Length == 0 || !Types.Contains(upload.ContentType, StringComparer.OrdinalIgnoreCase))
            throw new ArgumentException("The avatar upload is invalid.", nameof(upload));

        try
        {
            CloudinaryUploadResponse response = await client.UploadAsync(new CloudinaryUploadRequest(
                upload.PublicId, upload.ContentType, upload.Bytes, Overwrite: false,
                UseFilename: false, UniqueFilename: false, DiscardOriginalFilename: true), cancellationToken);
            if (response.Outcome != CloudinaryUploadOutcome.Succeeded || response.SecureUrl is null ||
                !response.SecureUrl.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(response.PublicId, upload.PublicId, StringComparison.Ordinal))
                return TravelerAvatarUploadResult.Failed("TRAVELER_AVATAR_STORAGE_UNAVAILABLE");
            return TravelerAvatarUploadResult.Succeeded(response.SecureUrl);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Traveler avatar upload failed.");
            return TravelerAvatarUploadResult.Failed("TRAVELER_AVATAR_STORAGE_UNAVAILABLE");
        }
    }

    public async Task<bool> DeleteAsync(string publicId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(publicId) || !publicId.StartsWith($"{Root}/", StringComparison.Ordinal))
            throw new ArgumentException("The avatar public id is invalid.", nameof(publicId));
        try
        {
            CloudinaryDeleteOutcome outcome = await client.DestroyAsync(new CloudinaryDeleteRequest(publicId, Invalidate: true), cancellationToken);
            return outcome is CloudinaryDeleteOutcome.Deleted or CloudinaryDeleteOutcome.AlreadyAbsent;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Traveler avatar cleanup failed.");
            return false;
        }
    }
}