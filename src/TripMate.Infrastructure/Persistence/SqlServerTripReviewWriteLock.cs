using Microsoft.EntityFrameworkCore;

using TripMate.Application.Features.TripReviews.Common;

namespace TripMate.Infrastructure.Persistence;

public sealed class SqlServerTripReviewWriteLock(ApplicationDbContext dbContext) : ITripReviewWriteLock
{
    public Task AcquireAsync(long bookingId, CancellationToken cancellationToken) =>
        AcquireAsync(ReviewableRecordRef.CommerceBooking(bookingId), cancellationToken);

    public async Task AcquireAsync(ReviewableRecordRef reviewableRecord, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(reviewableRecord);
        if (dbContext.Database.CurrentTransaction is null)
            throw new InvalidOperationException("Trip review write lock requires a caller-owned transaction.");

        var resource = reviewableRecord.LockKey;
        await dbContext.Database.ExecuteSqlInterpolatedAsync($"""
            DECLARE @lockResult INT;
            EXEC @lockResult = sys.sp_getapplock
                @Resource = {resource},
                @LockMode = 'Exclusive',
                @LockOwner = 'Transaction',
                @LockTimeout = 15000;

            IF @lockResult < 0
            BEGIN
                THROW 51002, 'Could not acquire the trip review write lock.', 1;
            END;
            """, cancellationToken);
    }
}