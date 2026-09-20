namespace TripMate.Application.Features.Scheduling.Common;

/// <summary>
/// Serializes retries for the same scheduling operation.
/// Its lifetime is the surrounding serializable database transaction.
/// </summary>
public interface ISchedulingRequestLock
{
    Task AcquireAsync(
        long travelerUserId,
        Guid idempotencyKey,
        CancellationToken cancellationToken);
}