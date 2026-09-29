namespace TripMate.Application.Common.Interfaces;

/// <summary>
/// Serializes create-group requests that reuse the same traveler idempotency key.
/// The lock lifetime is the surrounding database transaction.
/// </summary>
public interface ITravelGroupCreationLock
{
    Task AcquireAsync(
        long travelerUserId,
        Guid idempotencyKey,
        CancellationToken cancellationToken);
}