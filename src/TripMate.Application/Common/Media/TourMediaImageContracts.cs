namespace TripMate.Application.Common.Media;

public static class TourMediaImagePolicy
{
    public const long MaxEncodedBytes = 10 * 1024 * 1024;
    public const long MaxPixelArea = 24_000_000;
}

public sealed record TourMediaImageSource(
    Stream Content,
    long Length,
    string FileName,
    string DeclaredContentType);

public sealed record InspectedTourMediaImage(
    byte[] Bytes,
    string ContentType,
    string FileExtension,
    int Width,
    int Height,
    string Sha256Hex);

public enum TourMediaImageRejectionReason
{
    EmptyFile = 1,
    FileTooLarge = 2,
    UnsupportedFormat = 3,
    MetadataMismatch = 4,
    CorruptImage = 5,
    InvalidDimensions = 6,
    PixelAreaTooLarge = 7,
}

public sealed record TourMediaImageInspectionResult(
    InspectedTourMediaImage? Image,
    TourMediaImageRejectionReason? RejectionReason,
    string? SafeErrorCode)
{
    public bool IsAccepted => Image is not null;

    public static TourMediaImageInspectionResult Accepted(InspectedTourMediaImage image)
    {
        ArgumentNullException.ThrowIfNull(image);
        return new TourMediaImageInspectionResult(image, null, null);
    }

    public static TourMediaImageInspectionResult Rejected(
        TourMediaImageRejectionReason reason,
        string safeErrorCode) =>
        new(null, reason, safeErrorCode);
}

public interface ITourMediaImageInspector
{
    Task<TourMediaImageInspectionResult> InspectAsync(
        TourMediaImageSource source,
        CancellationToken cancellationToken);
}