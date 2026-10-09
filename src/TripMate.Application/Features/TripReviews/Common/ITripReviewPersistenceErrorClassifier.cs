using Microsoft.EntityFrameworkCore;

namespace TripMate.Application.Features.TripReviews.Common;

public interface ITripReviewPersistenceErrorClassifier
{
    bool IsBookingDuplicate(DbUpdateException exception);
}