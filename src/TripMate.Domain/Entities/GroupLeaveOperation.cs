using TripMate.Domain.Common;

namespace TripMate.Domain.Entities;

/// <summary>Preserves a successful leave response across retries, including after rejoining.</summary>
public sealed class GroupLeaveOperation : BaseEntity
{
    private GroupLeaveOperation()
    {
    }

    private GroupLeaveOperation(
        long travelerUserId,
        long groupId,
        Guid idempotencyKey,
        bool groupClosed,
        long? newHostUserId,
        DateTimeOffset leftAtUtc)
    {
        if (travelerUserId <= 0 || groupId <= 0)
        {
            throw new ArgumentOutOfRangeException(travelerUserId <= 0 ? nameof(travelerUserId) : nameof(groupId));
        }

        if (idempotencyKey == Guid.Empty)
        {
            throw new ArgumentException("An idempotency key is required.", nameof(idempotencyKey));
        }

        if (newHostUserId is <= 0 || (groupClosed && newHostUserId.HasValue))
        {
            throw new ArgumentException("A closed group cannot have a successor Host.", nameof(newHostUserId));
        }

        TravelerUserId = travelerUserId;
        GroupId = groupId;
        IdempotencyKey = idempotencyKey;
        GroupClosed = groupClosed;
        NewHostUserId = newHostUserId;
        LeftAtUtc = leftAtUtc.ToUniversalTime();
    }

    public static GroupLeaveOperation Create(
        long travelerUserId,
        long groupId,
        Guid idempotencyKey,
        bool groupClosed,
        long? newHostUserId,
        DateTimeOffset leftAtUtc) =>
        new(travelerUserId, groupId, idempotencyKey, groupClosed, newHostUserId, leftAtUtc);

    public long TravelerUserId { get; private set; }

    public long GroupId { get; private set; }

    public Guid IdempotencyKey { get; private set; }

    public bool GroupClosed { get; private set; }

    public long? NewHostUserId { get; private set; }

    public DateTimeOffset LeftAtUtc { get; private set; }
}