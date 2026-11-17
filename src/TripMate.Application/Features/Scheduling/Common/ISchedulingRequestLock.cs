namespace TripMate.Application.Features.Scheduling.Common;

/// <summary>
/// Serializes scheduling DB phases for the same traveler, across idempotency keys.
/// Its lifetime is the surrounding serializable database transaction, with a bounded wait.
/// </summary>
public interface ISchedulingRequestLock
{
    Task AcquireAsync(
        long travelerUserId,
        Guid idempotencyKey,
        CancellationToken cancellationToken);
}