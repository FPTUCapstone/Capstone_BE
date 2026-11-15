namespace TripMate.Application.Common.Interfaces;

/// <summary>Transaction-scoped locks for a leave request and its group's lifecycle.</summary>
public interface IGroupLeaveLock
{
    Task AcquireOperationLockAsync(long travelerUserId, Guid idempotencyKey, CancellationToken cancellationToken);

    Task AcquireGroupLockAsync(long groupId, CancellationToken cancellationToken);
}