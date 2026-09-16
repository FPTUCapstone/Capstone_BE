using TripMate.Domain.Common;
using TripMate.Domain.Enums;

namespace TripMate.Domain.Entities;

/// <summary>
/// Stores the result of an invitation operation so a retried HTTP request can return the same invitation.
/// </summary>
public sealed class GroupInvitationOperation : BaseEntity
{
    private GroupInvitationOperation()
    {
    }

    private GroupInvitationOperation(
        long travelerUserId,
        long groupId,
        GroupInvitationOperationType operationType,
        Guid idempotencyKey,
        GroupInvitation invitation,
        DateTimeOffset createdAtUtc)
    {
        if (travelerUserId <= 0 || groupId <= 0)
        {
            throw new ArgumentOutOfRangeException(travelerUserId <= 0 ? nameof(travelerUserId) : nameof(groupId));
        }

        if (idempotencyKey == Guid.Empty)
        {
            throw new ArgumentException("An idempotency key is required.", nameof(idempotencyKey));
        }

        ArgumentNullException.ThrowIfNull(invitation);
        if (invitation.GroupId != groupId || invitation.CreatedBy != travelerUserId)
        {
            throw new ArgumentException("The invitation must belong to the operation host and group.", nameof(invitation));
        }

        TravelerUserId = travelerUserId;
        GroupId = groupId;
        OperationType = operationType;
        IdempotencyKey = idempotencyKey;
        Invitation = invitation;
        CreatedAtUtc = createdAtUtc;
    }

    public static GroupInvitationOperation Create(
        long travelerUserId,
        long groupId,
        GroupInvitationOperationType operationType,
        Guid idempotencyKey,
        GroupInvitation invitation,
        DateTimeOffset createdAtUtc) =>
        new(travelerUserId, groupId, operationType, idempotencyKey, invitation, createdAtUtc);

    public long TravelerUserId { get; private set; }

    public long GroupId { get; private set; }

    public GroupInvitationOperationType OperationType { get; private set; }

    public Guid IdempotencyKey { get; private set; }

    public long InvitationId { get; private set; }

    public GroupInvitation Invitation { get; private set; } = null!;

    public DateTimeOffset CreatedAtUtc { get; private set; }
}