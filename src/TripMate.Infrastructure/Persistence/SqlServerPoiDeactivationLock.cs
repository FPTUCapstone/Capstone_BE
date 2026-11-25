using System.Globalization;

using Microsoft.EntityFrameworkCore;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Features.PointsOfInterest.Deactivate;

namespace TripMate.Infrastructure.Persistence;

public sealed class SqlServerPoiDeactivationLock(IApplicationDbContext db) : IPoiDeactivationLock
{
    public async Task AcquireAsync(long poiId, CancellationToken cancellationToken)
    {
        if (db is not DbContext efDbContext || !efDbContext.Database.IsRelational())
        {
            return;
        }

        if (efDbContext.Database.CurrentTransaction is null)
        {
            throw new InvalidOperationException("POI deactivation requires an active SQL transaction.");
        }

        // Match TM-225's ordinal lock ordering for the shared provisioning resource.
        var id = poiId.ToString(CultureInfo.InvariantCulture);
        string[] resources = ["TripMate:PoiMutation:" + id, "TripMate:Provisioning:Poi:" + id];

        foreach (var resource in resources)
        {
            await efDbContext.Database.ExecuteSqlInterpolatedAsync($"""
            DECLARE @lockResult INT;
            EXEC @lockResult = sys.sp_getapplock
                @Resource = {resource},
                @LockMode = 'Exclusive',
                @LockOwner = 'Transaction',
                @LockTimeout = 15000;
            IF @lockResult < 0
                THROW 51003, 'Could not acquire the POI deactivation lock.', 1;
            """, cancellationToken);
        }
    }
}