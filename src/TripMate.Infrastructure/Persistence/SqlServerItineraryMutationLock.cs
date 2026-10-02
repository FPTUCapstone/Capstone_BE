using Microsoft.EntityFrameworkCore;

using TripMate.Application.Features.Itineraries.Common;

namespace TripMate.Infrastructure.Persistence;

public sealed class SqlServerItineraryMutationLock(ApplicationDbContext dbContext)
    : IItineraryMutationLock
{
    public async Task AcquireAsync(
        long mutationResourceId,
        CancellationToken cancellationToken)
    {
        var resource = $"TripMate:ItineraryMutation:{mutationResourceId}";

        await dbContext.Database.ExecuteSqlInterpolatedAsync($"""
            DECLARE @lockResult INT;
            EXEC @lockResult = sys.sp_getapplock
                @Resource = {resource},
                @LockMode = 'Exclusive',
                @LockOwner = 'Transaction',
                @LockTimeout = -1;

            IF @lockResult < 0
            BEGIN
                THROW 51002, 'Could not acquire the itinerary mutation lock.', 1;
            END;
            """, cancellationToken);
    }
}