using Microsoft.EntityFrameworkCore;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Features.Operator.Revenue.Exports.Common;
using TripMate.Domain.Enums;

namespace TripMate.Application.Features.Operator.Revenue.Exports.Worker;

/// <summary>Removes the private asset and transitions the record once an export's retention window passes.</summary>
public sealed class RevenueExportExpiryProcessor(
    IApplicationDbContext db, IDateTimeProvider clock, IRevenueExportStorage storage)
{
    public async Task<int> ExpireDueExportsAsync(CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;
        var due = await db.RevenueExports
            .Where(x => x.Status == RevenueExportStatus.Ready && x.ExpiresAtUtc <= now)
            .ToListAsync(cancellationToken);

        var expiredCount = 0;
        foreach (var export in due)
        {
            if (export.StorageKey is not null)
            {
                await storage.DeleteAsync(export.StorageKey, cancellationToken);
            }

            if (export.MarkExpired(now))
            {
                expiredCount++;
            }
        }

        if (expiredCount > 0)
        {
            await db.SaveChangesAsync(cancellationToken);
        }

        return expiredCount;
    }
}
