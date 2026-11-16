using System.Security.Cryptography;

using Microsoft.Extensions.Logging;

using TripMate.Application.Features.Traveler.PostTripComplaints.Common;

namespace TripMate.Infrastructure.Media.Cloudinary;

internal sealed class CloudinaryComplaintEvidenceStorage(
    ICloudinaryClient client,
    ILogger<CloudinaryComplaintEvidenceStorage> logger) : IComplaintEvidenceStorage
{
    private const string Root = "tripmate/complaints";

    public async Task<StoredComplaintEvidence?> StoreAsync(
        ComplaintEvidenceUpload upload, CancellationToken cancellationToken)
    {
        if (upload.Bytes.Length == 0 || !IsAllowed(upload.ContentType))
            throw new ArgumentException("Validated complaint evidence is required.", nameof(upload));
        bool isRaw = upload.ContentType.Equals("application/pdf", StringComparison.OrdinalIgnoreCase);
        string publicId = $"{Root}/{Guid.NewGuid():N}";
        try
        {
            CloudinaryUploadResponse result = await client.UploadAsync(new CloudinaryUploadRequest(
                publicId, upload.ContentType, upload.Bytes, Overwrite: false, UseFilename: false,
                UniqueFilename: false, DiscardOriginalFilename: true)
            { IsRaw = isRaw, IsPrivateDocument = true }, cancellationToken);
            if (result.Outcome != CloudinaryUploadOutcome.Succeeded || result.PublicId != publicId)
                return null;
            string key = (isRaw ? "raw:" : "image:") + publicId;
            string checksum = Convert.ToHexString(SHA256.HashData(upload.Bytes));
            return new StoredComplaintEvidence(key, checksum, upload.Bytes.LongLength);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Complaint evidence upload failed.");
            return null;
        }
    }

    public async Task<bool> DeleteAsync(string storageObjectKey, CancellationToken cancellationToken)
    {
        bool isRaw = storageObjectKey.StartsWith("raw:", StringComparison.Ordinal);
        string publicId = storageObjectKey[(isRaw ? "raw:".Length : "image:".Length)..];
        if (!publicId.StartsWith(Root + "/", StringComparison.Ordinal)) return false;
        try
        {
            CloudinaryDeleteOutcome result = await client.DestroyAsync(new CloudinaryDeleteRequest(publicId, Invalidate: true)
            { IsRaw = isRaw, IsPrivateDocument = true }, cancellationToken);
            return result is CloudinaryDeleteOutcome.Deleted or CloudinaryDeleteOutcome.AlreadyAbsent;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Complaint evidence cleanup failed.");
            return false;
        }
    }

    public Task<byte[]?> ReadAsync(string storageObjectKey, CancellationToken cancellationToken) =>
        Task.FromResult<byte[]?>(null);

    public Uri? CreateTemporaryDownloadUrl(string storageObjectKey, string contentType, DateTimeOffset expiresAtUtc)
    {
        bool isRaw = storageObjectKey.StartsWith("raw:", StringComparison.Ordinal);
        string prefix = isRaw ? "raw:" : "image:";
        if (!storageObjectKey.StartsWith(prefix, StringComparison.Ordinal)) return null;
        string publicId = storageObjectKey[prefix.Length..];
        if (!publicId.StartsWith(Root + "/", StringComparison.Ordinal) || publicId.Contains("..", StringComparison.Ordinal)) return null;
        string? format = contentType.ToLowerInvariant() switch
        {
            "application/pdf" when isRaw => "pdf",
            "image/jpeg" when !isRaw => "jpg",
            "image/png" when !isRaw => "png",
            "image/webp" when !isRaw => "webp",
            _ => null,
        };
        return format is null ? null : client.CreateTemporaryDownloadUrl(publicId, format, isRaw, expiresAtUtc);
    }

    private static bool IsAllowed(string contentType) =>
        contentType.Equals("image/jpeg", StringComparison.OrdinalIgnoreCase) ||
        contentType.Equals("image/png", StringComparison.OrdinalIgnoreCase) ||
        contentType.Equals("image/webp", StringComparison.OrdinalIgnoreCase) ||
        contentType.Equals("application/pdf", StringComparison.OrdinalIgnoreCase);
}
