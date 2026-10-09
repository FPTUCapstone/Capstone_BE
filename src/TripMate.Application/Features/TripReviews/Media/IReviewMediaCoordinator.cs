using TripMate.Application.Common.Models;
using TripMate.Application.Features.TripReviews.Common;

namespace TripMate.Application.Features.TripReviews.Media;

public static class ReviewMediaPreparationErrors
{
    public const string InvalidInput = "review_media.invalid_input";
    public const string StorageUnavailable = "review_media.storage_unavailable";
}

public interface IReviewMediaCoordinator
{
    Task<Result<PreparedReviewMedia>> PrepareAsync(
        long bookingId,
        long travelerId,
        IReadOnlyList<ReviewImageSource> images,
        CancellationToken ct);

    Task<Result<PreparedReviewMedia>> PrepareAsync(
        ReviewableRecordRef reviewableRecord,
        long travelerId,
        IReadOnlyList<ReviewImageSource> images,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(reviewableRecord);
        return reviewableRecord.IsCommerceBooking
            ? PrepareAsync(reviewableRecord.Id, travelerId, images, ct)
            : Task.FromException<Result<PreparedReviewMedia>>(
                new NotSupportedException("This media coordinator does not support service bookings."));
    }
}