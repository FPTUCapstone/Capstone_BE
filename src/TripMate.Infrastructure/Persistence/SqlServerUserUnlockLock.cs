using Microsoft.EntityFrameworkCore;

using TripMate.Application.Common.Interfaces;

namespace TripMate.Infrastructure.Persistence;

public sealed class SqlServerUserUnlockLock(ApplicationDbContext dbContext) : IUserUnlockLock
{
    public Task AcquireAsync(long administratorUserId, string idempotencyKey, CancellationToken cancellationToken)
    {
        var resource = $"TripMate:UserUnlock:{administratorUserId}:{idempotencyKey}";
        return dbContext.Database.ExecuteSqlInterpolatedAsync($"""
            DECLARE @lockResult INT;
            EXEC @lockResult = sys.sp_getapplock
                @Resource = {resource},
                @LockMode = 'Exclusive',
                @LockOwner = 'Transaction',
                @LockTimeout = -1;
            IF @lockResult < 0
                THROW 51000, 'Could not acquire the user unlock lock.', 1;
            """, cancellationToken);
    }

    public Task AcquireTargetUserAsync(long targetUserId, CancellationToken cancellationToken)
    {
        var resource = $"TripMate:UserUnlock:Target:{targetUserId}";
        return dbContext.Database.ExecuteSqlInterpolatedAsync($"""
            DECLARE @lockResult INT;
            EXEC @lockResult = sys.sp_getapplock
                @Resource = {resource},
                @LockMode = 'Exclusive',
                @LockOwner = 'Transaction',
                @LockTimeout = -1;
            IF @lockResult < 0
                THROW 51000, 'Could not acquire the target user unlock lock.', 1;
            """, cancellationToken);
    }
}