namespace TripMate.Application.Common.Media;

public sealed record LandingContentMediaUpload(string PublicId, string ContentType, byte[] Bytes);
public sealed record LandingContentMediaUploadResult(Uri? DeliveryUrl, string? SafeErrorCode)
{
    public bool IsSuccess => DeliveryUrl is not null;
}

public interface ILandingContentMediaStorage
{
    string AllocatePublicId();
    Task<LandingContentMediaUploadResult> UploadAsync(LandingContentMediaUpload upload, CancellationToken cancellationToken);
}