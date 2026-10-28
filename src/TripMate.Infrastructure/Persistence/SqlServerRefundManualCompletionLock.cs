using Microsoft.EntityFrameworkCore;

using TripMate.Application.Features.Bookings.Common;

namespace TripMate.Infrastructure.Persistence;

public sealed class SqlServerRefundManualCompletionLock(ApplicationDbContext dbContext)
    : IRefundManualCompletionLock
{
    public async Task AcquireAsync(long refundId, CancellationToken cancellationToken)
    {
        if (dbContext.Database.CurrentTransaction is null)
            throw new InvalidOperationException("Manual refund completion lock requires a caller-owned transaction.");

        var resource = $"TripMate:RefundManualCompletion:{refundId}";
        await dbContext.Database.ExecuteSqlInterpolatedAsync($"""
            DECLARE @lockResult INT;
            EXEC @lockResult = sys.sp_getapplock
                @Resource = {resource},
                @LockMode = 'Exclusive',
                @LockOwner = 'Transaction',
                @LockTimeout = 15000;
            IF @lockResult < 0
                THROW 51003, 'Could not acquire the manual refund completion lock.', 1;
            """, cancellationToken);
    }
}