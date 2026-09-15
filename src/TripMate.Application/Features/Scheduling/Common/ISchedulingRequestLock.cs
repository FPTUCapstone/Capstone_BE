namespace TripMate.Application.Features.Scheduling.Common;

/// <summary>
/// Serializes an operation retry and the per-traveler local-day quota check.
/// Its lifetime is the surrounding serializable database transaction.
/// </summary>
public interface ISchedulingRequestLock
{
    Task AcquireAsync(
        long travelerUserId,
        DateOnly localDate,
        Guid idempotencyKey,
        CancellationToken cancellationToken);
}
