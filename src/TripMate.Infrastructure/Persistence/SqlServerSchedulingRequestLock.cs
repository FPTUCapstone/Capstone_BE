using Microsoft.EntityFrameworkCore;

using TripMate.Application.Features.Scheduling.Common;

namespace TripMate.Infrastructure.Persistence;

public sealed class SqlServerSchedulingRequestLock(ApplicationDbContext dbContext)
    : ISchedulingRequestLock
{
    public async Task AcquireAsync(
        long travelerUserId,
        Guid idempotencyKey,
        CancellationToken cancellationToken)
    {
        var keyResource = $"TripMate:SchedulingRequest:{travelerUserId}:{idempotencyKey:N}";

        await AcquireResourceAsync(keyResource, cancellationToken);
    }

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
                THROW 51001, 'Could not acquire the scheduling request lock.', 1;
            END;
            """, cancellationToken);
    }
}