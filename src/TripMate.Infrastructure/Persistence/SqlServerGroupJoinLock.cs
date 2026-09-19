using Microsoft.EntityFrameworkCore;

using TripMate.Application.Common.Interfaces;

namespace TripMate.Infrastructure.Persistence;

/// <summary>
/// Serializes the idempotency operation, invitation code, and group lifecycle with transaction-scoped SQL Server locks.
/// </summary>
public sealed class SqlServerGroupJoinLock(ApplicationDbContext dbContext) : IGroupJoinLock
{
    public Task AcquireOperationLockAsync(long travelerUserId, Guid idempotencyKey, CancellationToken cancellationToken) =>
        AcquireResourceAsync($"TripMate:GroupJoinOperation:{travelerUserId}:{idempotencyKey:N}", cancellationToken);

    public Task AcquireCodeLockAsync(string normalizedInviteCode, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(normalizedInviteCode);
        return AcquireResourceAsync($"TripMate:GroupInvitationCode:{normalizedInviteCode.Trim().ToUpperInvariant()}", cancellationToken);
    }

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
                THROW 51000, 'Could not acquire the group join lock.', 1;
            END;
            """, cancellationToken);
}