namespace TripMate.Application.Common.Interfaces;

public interface IUserUnlockLock
{
    Task AcquireAsync(long administratorUserId, string idempotencyKey, CancellationToken cancellationToken);

    Task AcquireTargetUserAsync(long targetUserId, CancellationToken cancellationToken);
}