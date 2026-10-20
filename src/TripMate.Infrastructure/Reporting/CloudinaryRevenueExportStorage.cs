using Microsoft.Extensions.Options;

using TripMate.Application.Features.Operator.Revenue.Exports.Common;
using TripMate.Infrastructure.Media.Cloudinary;

namespace TripMate.Infrastructure.Reporting;

internal sealed class CloudinaryRevenueExportStorage(
    ICloudinaryClient client,
    IOptions<CloudinaryOptions> options) : IRevenueExportStorage
{
    private const string UnavailableErrorCode = "REVENUE_EXPORT_STORAGE_UNAVAILABLE";
    private const string RejectedErrorCode = "REVENUE_EXPORT_STORAGE_REJECTED";
    private const string InvalidResponseErrorCode = "REVENUE_EXPORT_STORAGE_INVALID_RESPONSE";

    private readonly CloudinaryOptions options = options.Value;

    public string AllocatePublicId(long operatorUserId, long exportId) =>
        $"{this.options.RevenueExportsFolderRoot}/{operatorUserId}/{exportId}-{Guid.NewGuid():N}";

    public async Task<RevenueExportStorageUploadResult> UploadAsync(
        RevenueExportStorageUpload upload, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(upload);
        if (string.IsNullOrWhiteSpace(upload.PublicId) ||
            !upload.PublicId.StartsWith($"{this.options.RevenueExportsFolderRoot}/", StringComparison.Ordinal))
        {
            throw new ArgumentException("A valid allocated export storage identifier is required.", nameof(upload));
        }

        if (upload.Bytes.Length == 0)
        {
            throw new ArgumentException("Export file content is required.", nameof(upload));
        }

        var uploadRequest = new CloudinaryUploadRequest(
            upload.PublicId,
            upload.ContentType,
            upload.Bytes,
            Overwrite: false,
            UseFilename: false,
            UniqueFilename: false,
            DiscardOriginalFilename: true)
        {
            IsRaw = true,
            IsPrivateDocument = true,
        };

        var response = await client.UploadAsync(uploadRequest, cancellationToken);

        if (response.Outcome == CloudinaryUploadOutcome.Succeeded)
        {
            if (string.IsNullOrWhiteSpace(response.PublicId) ||
                !response.PublicId.Equals(upload.PublicId, StringComparison.Ordinal))
            {
                return RevenueExportStorageUploadResult.Failed(InvalidResponseErrorCode);
            }

            return RevenueExportStorageUploadResult.Succeeded(response.PublicId);
        }

        return RevenueExportStorageUploadResult.Failed(
            response.SafeErrorCode ?? (response.Outcome == CloudinaryUploadOutcome.TransientFailure
                ? UnavailableErrorCode
                : RejectedErrorCode));
    }

    public async Task<bool> DeleteAsync(string storageKey, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(storageKey) ||
            !storageKey.StartsWith($"{this.options.RevenueExportsFolderRoot}/", StringComparison.Ordinal))
        {
            throw new ArgumentException("A valid allocated export storage identifier is required.", nameof(storageKey));
        }

        var outcome = await client.DestroyAsync(
            new CloudinaryDeleteRequest(storageKey, Invalidate: true) { IsRaw = true, IsPrivateDocument = true },
            cancellationToken);

        return outcome is CloudinaryDeleteOutcome.Deleted or CloudinaryDeleteOutcome.AlreadyAbsent;
    }

    public Uri? CreateTemporaryDownloadUrl(string storageKey, DateTimeOffset expiresAtUtc)
    {
        if (string.IsNullOrWhiteSpace(storageKey) ||
            !storageKey.StartsWith($"{this.options.RevenueExportsFolderRoot}/", StringComparison.Ordinal) ||
            storageKey.Contains("..", StringComparison.Ordinal))
        {
            return null;
        }

        return client.CreateTemporaryDownloadUrl(storageKey, "xlsx", isRaw: true, expiresAtUtc);
    }
}
