using Microsoft.EntityFrameworkCore;

using TripMate.Application.Common.Interfaces;

namespace TripMate.Infrastructure.Persistence;

/// <summary>
/// Uses a transaction-scoped SQL Server application lock so concurrent retries with the same
/// idempotency key cannot both observe a missing creation record.
/// </summary>
public sealed class SqlServerTravelGroupCreationLock(ApplicationDbContext dbContext)
    : ITravelGroupCreationLock
{
    public async Task AcquireAsync(
        long travelerUserId,
        Guid idempotencyKey,
        CancellationToken cancellationToken)
    {
        var resource = $"TripMate:TravelGroupCreation:{travelerUserId}:{idempotencyKey:N}";

        await dbContext.Database.ExecuteSqlInterpolatedAsync($"""
            DECLARE @lockResult INT;
            EXEC @lockResult = sys.sp_getapplock
                @Resource = {resource},
                @LockMode = 'Exclusive',
                @LockOwner = 'Transaction',
                @LockTimeout = -1;

            IF @lockResult < 0
            BEGIN
                THROW 51000, 'Could not acquire the travel group creation lock.', 1;
            END;
            """, cancellationToken);
    }
}