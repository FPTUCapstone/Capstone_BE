namespace TripMate.Application.Common.Interfaces;

/// <summary>
/// Serializes invitation operations for both the requested group and the idempotency key.
/// The lock lifetime is the surrounding database transaction.
/// </summary>
public interface IGroupInvitationLock
{
    Task AcquireAsync(
        long groupId,
        long travelerUserId,
        Guid idempotencyKey,
        CancellationToken cancellationToken);
}