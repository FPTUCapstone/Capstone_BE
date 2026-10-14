using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

using TripMate.Application.Features.TripReviews.Common;

namespace TripMate.Infrastructure.Persistence;

public sealed class SqlServerTripReviewPersistenceErrorClassifier : ITripReviewPersistenceErrorClassifier
{
    private static readonly string[] QuotedBookingIndexes =
    [
        "'UX_TripReviews_CommerceBooking'",
        "'UX_TripReviews_ServiceBooking'",
        "'UX_TripReviews_Booking'",
    ];

    public bool IsBookingDuplicate(DbUpdateException exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current is SqlException sqlException
                && sqlException.Number is 2601 or 2627
                && QuotedBookingIndexes.Any(index =>
                    sqlException.Message.Contains(index, StringComparison.Ordinal)))
            {
                return true;
            }
        }

        return false;
    }
}