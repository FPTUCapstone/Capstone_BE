using Microsoft.EntityFrameworkCore;

using TripMate.Application.Common.Interfaces;

namespace TripMate.Infrastructure.Persistence;

/// <summary>
/// UC-07: mirrors SqlServerItineraryMutationLock's sp_getapplock pattern for a different
/// resource. Enforced by SQL Server itself (@LockOwner = 'Transaction'), so it serializes
/// concurrent change-password attempts for one account across every API process/container —
/// not only within one process, unlike an in-memory semaphore.
/// </summary>
public sealed class SqlServerAccountCredentialLock(ApplicationDbContext dbContext)
    : IAccountCredentialLock
{
    public async Task AcquireAsync(long userId, CancellationToken cancellationToken)
    {
        var resource = $"TripMate:AccountCredential:{userId}";

        await dbContext.Database.ExecuteSqlInterpolatedAsync($"""
            DECLARE @lockResult INT;
            EXEC @lockResult = sys.sp_getapplock
                @Resource = {resource},
                @LockMode = 'Exclusive',
                @LockOwner = 'Transaction',
                @LockTimeout = -1;

            IF @lockResult < 0
            BEGIN
                THROW 51003, 'Could not acquire the account credential lock.', 1;
            END;
            """, cancellationToken);
    }
}