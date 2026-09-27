using Microsoft.EntityFrameworkCore;

using TripMate.Application.Common.Interfaces;

namespace TripMate.Infrastructure.Persistence;

/// <summary>
/// Serializes a tour's media mutations across application instances.  The tour
/// resource is always acquired before the idempotency-operation resource so
/// callers cannot create a lock-order cycle.
/// </summary>
public sealed class SqlServerTourMediaUploadLock(ApplicationDbContext dbContext)
    : ITourMediaUploadLock
{
    public async Task AcquireOperationAsync(
        long tourId,
        long actorUserId,
        Guid idempotencyKey,
        CancellationToken cancellationToken)
    {
        await AcquireTourAsync(tourId, cancellationToken);
        await AcquireResourceAsync(
            $"TripMate:TourMedia:Operation:{tourId}:{actorUserId}:{idempotencyKey:N}",
            cancellationToken);
    }

    public Task AcquireTourAsync(long tourId, CancellationToken cancellationToken) =>
        AcquireResourceAsync($"TripMate:TourMedia:Tour:{tourId}", cancellationToken);

    private async Task AcquireResourceAsync(string resource, CancellationToken cancellationToken)
    {
        await dbContext.Database.ExecuteSqlInterpolatedAsync($"""
            DECLARE @lockResult INT;
            EXEC @lockResult = sys.sp_getapplock
                @Resource = {resource},
                @LockMode = 'Exclusive',
                @LockOwner = 'Transaction',
                @LockTimeout = -1;

            IF @lockResult < 0
            BEGIN
                THROW 51001, 'Could not acquire the tour media upload lock.', 1;
            END;
            """, cancellationToken);
    }
}