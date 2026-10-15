namespace TripMate.Application.Common.Media;

public sealed record TravelerAvatarUpload(string PublicId, string ContentType, byte[] Bytes);
public sealed record TravelerAvatarUploadResult(Uri? DeliveryUrl, string? SafeErrorCode)
{
    public bool IsSuccess => DeliveryUrl is not null;
    public static TravelerAvatarUploadResult Succeeded(Uri url) => new(url, null);
    public static TravelerAvatarUploadResult Failed(string code) => new(null, code);
}

public interface ITravelerAvatarStorage
{
    string AllocatePublicId(long userId);
    Task<TravelerAvatarUploadResult> UploadAsync(TravelerAvatarUpload upload, CancellationToken cancellationToken);
    Task<bool> DeleteAsync(string publicId, CancellationToken cancellationToken);
}