using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

using TripMate.Application.Features.Bookings.Common;

namespace TripMate.Infrastructure.Persistence;

public sealed class SqlServerBookingCancellationPersistenceErrorClassifier : IBookingCancellationPersistenceErrorClassifier
{
    private const string IdempotencyIndex = "UX_BookingCancellationOperations_ActorKey";

    public bool IsIdempotencyKeyConflict(DbUpdateException exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current is SqlException { Number: 2601 or 2627 } sqlException
                && sqlException.Message.Contains(IdempotencyIndex, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }
}