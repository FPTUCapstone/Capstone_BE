namespace TripMate.Application.Features.TripReviews.Common;

/// <summary>
/// Serializes review submissions targeting the same booking.
/// The lock lifetime is the surrounding database transaction.
/// </summary>
public interface ITripReviewWriteLock
{
    Task AcquireAsync(long bookingId, CancellationToken cancellationToken);

    Task AcquireAsync(ReviewableRecordRef reviewableRecord, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(reviewableRecord);
        return reviewableRecord.IsCommerceBooking
            ? AcquireAsync(reviewableRecord.Id, cancellationToken)
            : Task.FromException(new NotSupportedException("This write lock does not support service bookings."));
    }
}