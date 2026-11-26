namespace TripMate.Application.Common.Media;

public sealed record OperatorLogoUpload(string PublicId, string ContentType, byte[] Bytes);

public sealed record OperatorLogoUploadResult(Uri? DeliveryUrl, string? SafeErrorCode)
{
    public bool IsSuccess => DeliveryUrl is not null;

    public static OperatorLogoUploadResult Succeeded(Uri url) => new(url, null);

    public static OperatorLogoUploadResult Failed(string code) => new(null, code);
}

/// <summary>Public (unsigned) storage for the Tour Operator logo shown to Travelers (UC-34, BR-16).</summary>
public interface IOperatorLogoStorage
{
    /// <summary>A new unique public id under the operator-logo root for this operator.</summary>
    string AllocatePublicId(long operatorUserId);

    Task<OperatorLogoUploadResult> UploadAsync(OperatorLogoUpload upload, CancellationToken cancellationToken);

    /// <summary>True when the asset is gone (deleted or already absent); false when it may still exist.</summary>
    Task<bool> DeleteAsync(string publicId, CancellationToken cancellationToken);
}