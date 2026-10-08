namespace TripMate.Infrastructure.Media.Cloudinary;

internal sealed record CloudinaryUploadRequest(
    string PublicId,
    string ContentType,
    byte[] Bytes,
    bool Overwrite,
    bool UseFilename,
    bool UniqueFilename,
    bool DiscardOriginalFilename)
{
    /// <summary>Raw uploads (documents) use ResourceType.raw instead of the image pipeline.</summary>
    public bool IsRaw { get; init; }
    public bool IsPrivateDocument { get; init; }
}

internal enum CloudinaryUploadOutcome
{
    Succeeded = 1,
    TransientFailure = 2,
    Rejected = 3,
}

internal sealed record CloudinaryUploadResponse(
    CloudinaryUploadOutcome Outcome,
    string? PublicId,
    Uri? SecureUrl,
    string? SafeErrorCode)
{
    public static CloudinaryUploadResponse Succeeded(string publicId, Uri secureUrl) =>
        new(CloudinaryUploadOutcome.Succeeded, publicId, secureUrl, null);

    public static CloudinaryUploadResponse TransientFailure(string safeErrorCode) =>
        new(CloudinaryUploadOutcome.TransientFailure, null, null, safeErrorCode);

    public static CloudinaryUploadResponse Rejected(string safeErrorCode) =>
        new(CloudinaryUploadOutcome.Rejected, null, null, safeErrorCode);
}

internal sealed record CloudinaryDeleteRequest(string PublicId, bool Invalidate)
{
    public bool IsRaw { get; init; }
    public bool IsPrivateDocument { get; init; }
}

internal enum CloudinaryDeleteOutcome
{
    Deleted = 1,
    AlreadyAbsent = 2,
    TransientFailure = 3,
    PermanentFailure = 4,
}

internal interface ICloudinaryClient
{
    Task<CloudinaryUploadResponse> UploadAsync(
        CloudinaryUploadRequest request,
        CancellationToken cancellationToken);

    Task<CloudinaryDeleteOutcome> DestroyAsync(
        CloudinaryDeleteRequest request,
        CancellationToken cancellationToken);

    Uri? CreateTemporaryDownloadUrl(string publicId, string format, bool isRaw,
        DateTimeOffset expiresAtUtc);
}