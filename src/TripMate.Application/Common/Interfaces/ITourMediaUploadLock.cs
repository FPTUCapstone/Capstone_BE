namespace TripMate.Application.Common.Interfaces;

/// <summary>
/// Serializes short Tour-media upload claim and completion transactions.
/// Provider I/O must never occur while one of these transaction-owned locks is held.
/// </summary>
public interface ITourMediaUploadLock
{
    Task AcquireOperationAsync(
        long tourId,
        long actorUserId,
        Guid idempotencyKey,
        CancellationToken cancellationToken);

    Task AcquireTourAsync(long tourId, CancellationToken cancellationToken);
}