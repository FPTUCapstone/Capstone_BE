namespace TripMate.Application.Common.Interfaces;

/// <summary>
/// Serializes short Tour-media upload claim and completion transactions.
/// Callers must acquire these locks inside an active SQL transaction on the same
/// DbContext connection; SQL Server uses sp_getapplock with LockOwner=Transaction.
/// The lock is held until that transaction commits or rolls back.
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