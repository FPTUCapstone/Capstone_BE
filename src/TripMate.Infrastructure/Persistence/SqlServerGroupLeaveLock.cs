using Microsoft.EntityFrameworkCore;

using TripMate.Application.Common.Interfaces;

namespace TripMate.Infrastructure.Persistence;

/// <summary>Shares the group lifecycle resource with join and invitation commands.</summary>
public sealed class SqlServerGroupLeaveLock(ApplicationDbContext dbContext) : IGroupLeaveLock
{
    public Task AcquireOperationLockAsync(long travelerUserId, Guid idempotencyKey, CancellationToken cancellationToken) =>
        AcquireResourceAsync($"TripMate:GroupLeaveOperation:{travelerUserId}:{idempotencyKey:N}", cancellationToken);

    public Task AcquireGroupLockAsync(long groupId, CancellationToken cancellationToken) =>
        AcquireResourceAsync($"TripMate:GroupInvitation:{groupId}", cancellationToken);

    private Task AcquireResourceAsync(string resource, CancellationToken cancellationToken) =>
        dbContext.Database.ExecuteSqlInterpolatedAsync($"""
            DECLARE @lockResult INT;
            EXEC @lockResult = sys.sp_getapplock
                @Resource = {resource},
                @LockMode = 'Exclusive',
                @LockOwner = 'Transaction',
                @LockTimeout = -1;

            IF @lockResult < 0
            BEGIN
                THROW 51000, 'Could not acquire the group leave lock.', 1;
            END;
            """, cancellationToken);
}