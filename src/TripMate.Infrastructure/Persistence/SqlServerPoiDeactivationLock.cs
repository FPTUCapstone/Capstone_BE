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

        // Lock POI mutation and coordinate with TM-225 account provisioning in alphabetical order
        var resources = new List<string>
        {
            "TripMate:PoiMutation:" + poiId.ToString(CultureInfo.InvariantCulture),
            "TripMate:Provisioning:Poi:" + poiId.ToString(CultureInfo.InvariantCulture),
        };
        resources.Sort(StringComparer.Ordinal);

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