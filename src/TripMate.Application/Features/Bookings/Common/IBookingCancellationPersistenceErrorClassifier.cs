using Microsoft.EntityFrameworkCore;

namespace TripMate.Application.Features.Bookings.Common;

public interface IBookingCancellationPersistenceErrorClassifier
{
    bool IsIdempotencyKeyConflict(DbUpdateException exception);
}