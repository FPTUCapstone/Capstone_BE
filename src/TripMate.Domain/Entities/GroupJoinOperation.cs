using TripMate.Domain.Common;

namespace TripMate.Domain.Entities;

/// <summary>
/// Stores the result of a group join operation so a retried HTTP request can return the same travel group.
/// </summary>
public sealed class GroupJoinOperation : BaseEntity
{
    private GroupJoinOperation()
    {
    }

    private GroupJoinOperation(
        long travelerUserId,
        long groupId,
        long invitationId,
        string invitationCode,
        Guid idempotencyKey,
        DateTimeOffset createdAtUtc)
    {
        if (travelerUserId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(travelerUserId));
        }

        if (groupId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(groupId));
        }

        if (invitationId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(invitationId));
        }

        if (string.IsNullOrWhiteSpace(invitationCode))
        {
            throw new ArgumentException("An invitation code is required.", nameof(invitationCode));
        }

        if (idempotencyKey == Guid.Empty)
        {
            throw new ArgumentException("An idempotency key is required.", nameof(idempotencyKey));
        }

        TravelerUserId = travelerUserId;
        GroupId = groupId;
        InvitationId = invitationId;
        InvitationCode = invitationCode.Trim().ToUpperInvariant();
        IdempotencyKey = idempotencyKey;
        CreatedAtUtc = createdAtUtc;
    }

    public static GroupJoinOperation Create(
        long travelerUserId,
        long groupId,
        long invitationId,
        string invitationCode,
        Guid idempotencyKey,
        DateTimeOffset createdAtUtc) =>
        new(travelerUserId, groupId, invitationId, invitationCode, idempotencyKey, createdAtUtc);

    public long TravelerUserId { get; private set; }

    public User TravelerUser { get; private set; } = null!;

    public long GroupId { get; private set; }

    public TravelGroup TravelGroup { get; private set; } = null!;

    public long InvitationId { get; private set; }

    public GroupInvitation Invitation { get; private set; } = null!;

    public string InvitationCode { get; private set; } = string.Empty;

    public Guid IdempotencyKey { get; private set; }

    public DateTimeOffset CreatedAtUtc { get; private set; }
}