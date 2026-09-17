namespace TripMate.Application.Common.Interfaces;

public interface IGroupJoinLock
{
    Task AcquireOperationLockAsync(long travelerUserId, Guid idempotencyKey, CancellationToken cancellationToken);

    Task AcquireCodeLockAsync(string normalizedInviteCode, CancellationToken cancellationToken);

    Task AcquireGroupLockAsync(long groupId, CancellationToken cancellationToken);
}
