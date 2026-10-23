namespace TripMate.Application.Features.TripReviews.Media;

/// <summary>Caller owns Content. DeclaredLength is advisory, never an actual-byte bound.</summary>
public sealed record ReviewImageSource(Stream Content, string FileName, string ContentType, long? DeclaredLength = null);
public sealed record InspectedReviewImage(byte[] Bytes, string ContentType, string Extension, int Width, int Height);
public enum ReviewImageRejection { EmptyFile, FileTooLarge, UnsupportedFormat, InvalidImage, AnimatedImage, ResourceLimit, MetadataMismatch }
public sealed record ReviewImageInspection(InspectedReviewImage? Image, ReviewImageRejection? Rejection)
{
    public bool IsAccepted => Image is not null && Rejection is null;
}
public interface IReviewImageInspector
{
    Task<ReviewImageInspection> InspectAsync(ReviewImageSource source, CancellationToken cancellationToken);
}