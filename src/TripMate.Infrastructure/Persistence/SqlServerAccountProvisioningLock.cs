using System.Globalization;
using System.Security.Cryptography;
using System.Text;

using Microsoft.EntityFrameworkCore;

using TripMate.Application.Features.AccountProvisioning;

namespace TripMate.Infrastructure.Persistence;

public sealed class SqlServerAccountProvisioningLock(ApplicationDbContext db) : IAccountProvisioningLock
{
    public async Task AcquireAsync(string normalizedEmail, string normalizedPhone, IReadOnlyCollection<long> poiIds,
        CancellationToken cancellationToken)
    {
        var resources = new List<string>
        {
            "TripMate:Provisioning:Email:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalizedEmail))),
            "TripMate:Provisioning:Phone:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalizedPhone))),
        };
        resources.AddRange(poiIds.Distinct().Select(id => "TripMate:Provisioning:Poi:" + id.ToString(CultureInfo.InvariantCulture)));
        resources.Sort(StringComparer.Ordinal);
        foreach (var resource in resources)
        {
            await db.Database.ExecuteSqlInterpolatedAsync($"""
            DECLARE @lockResult INT;
            EXEC @lockResult = sys.sp_getapplock
                @Resource = {resource},
                @LockMode = 'Exclusive',
                @LockOwner = 'Transaction',
                @LockTimeout = -1;
            IF @lockResult < 0
                THROW 51003, 'Could not acquire the account provisioning lock.', 1;
            """, cancellationToken);
        }
    }
}