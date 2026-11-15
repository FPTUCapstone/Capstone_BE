namespace TripMate.Application.Features.Operator.Revenue.Exports.Common;

public sealed record RevenueExportStorageUpload(string PublicId, string ContentType, byte[] Bytes);

public sealed record RevenueExportStorageUploadResult(string? StorageKey, string? SafeErrorCode)
{
    public bool IsSuccess => StorageKey is not null;

    public static RevenueExportStorageUploadResult Succeeded(string storageKey) => new(storageKey, null);

    public static RevenueExportStorageUploadResult Failed(string safeErrorCode) => new(null, safeErrorCode);
}

/// <summary>
/// Private object storage for generated export files. The persisted StorageKey is an opaque
/// reference, never a bearer download URL (see spec §Persistence) — only
/// <see cref="CreateTemporaryDownloadUrl"/> turns it into a short-lived, operator-authorized link.
/// </summary>
public interface IRevenueExportStorage
{
    /// <summary>Allocates a provider-safe public id; user-controlled tour names are never used as paths.</summary>
    string AllocatePublicId(long operatorUserId, long exportId);

    Task<RevenueExportStorageUploadResult> UploadAsync(
        RevenueExportStorageUpload upload, CancellationToken cancellationToken);

    /// <summary>Removes a partial or superseded asset; never exposed once an upload fails.</summary>
    Task<bool> DeleteAsync(string storageKey, CancellationToken cancellationToken);

    /// <summary>Returns a short-lived URL for an owned, ready export, or null for an invalid reference.</summary>
    Uri? CreateTemporaryDownloadUrl(string storageKey, DateTimeOffset expiresAtUtc);
}