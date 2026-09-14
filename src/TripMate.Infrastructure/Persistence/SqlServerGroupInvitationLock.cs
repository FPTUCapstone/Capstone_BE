using Microsoft.EntityFrameworkCore;

using TripMate.Application.Common.Interfaces;

namespace TripMate.Infrastructure.Persistence;

/// <summary>
/// Serializes the idempotency operation and group invitation lifecycle with transaction-scoped SQL Server locks.
/// </summary>
public sealed class SqlServerGroupInvitationLock(ApplicationDbContext dbContext) : IGroupInvitationLock
{
    public async Task AcquireAsync(
        long groupId,
        long travelerUserId,
        Guid idempotencyKey,
        CancellationToken cancellationToken)
    {
        var operationResource = $"TripMate:GroupInvitationOperation:{travelerUserId}:{idempotencyKey:N}";
        var groupResource = $"TripMate:GroupInvitation:{groupId}";

        await AcquireResourceAsync(operationResource, cancellationToken);
        await AcquireResourceAsync(groupResource, cancellationToken);
    }

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
                THROW 51000, 'Could not acquire the group invitation lock.', 1;
            END;
            """, cancellationToken);
}