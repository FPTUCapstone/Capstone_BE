using TripMate.Domain.Common;

namespace TripMate.Domain.Entities;

/// <summary>Preserves a successful member-removal response across retries and later rejoins.</summary>
public sealed class GroupMemberRemovalOperation : BaseEntity
{
    public const int MaxMemberNameLength = 150;

    private GroupMemberRemovalOperation()
    {
    }

    private GroupMemberRemovalOperation(
        long hostUserId,
        long groupId,
        long memberUserId,
        string memberName,
        Guid idempotencyKey,
        DateTimeOffset removedAtUtc)
    {
        if (hostUserId <= 0 || groupId <= 0 || memberUserId <= 0)
        {
            throw new ArgumentOutOfRangeException(
                hostUserId <= 0 ? nameof(hostUserId) : groupId <= 0 ? nameof(groupId) : nameof(memberUserId));
        }

        if (hostUserId == memberUserId)
        {
            throw new ArgumentException("The Host cannot remove themselves.", nameof(memberUserId));
        }

        if (string.IsNullOrWhiteSpace(memberName) || memberName.Length > MaxMemberNameLength)
        {
            throw new ArgumentException("A valid member name is required.", nameof(memberName));
        }

        if (idempotencyKey == Guid.Empty)
        {
            throw new ArgumentException("An idempotency key is required.", nameof(idempotencyKey));
        }

        HostUserId = hostUserId;
        GroupId = groupId;
        MemberUserId = memberUserId;
        MemberName = memberName;
        IdempotencyKey = idempotencyKey;
        RemovedAtUtc = removedAtUtc.ToUniversalTime();
    }

    public static GroupMemberRemovalOperation Create(
        long hostUserId,
        long groupId,
        long memberUserId,
        string memberName,
        Guid idempotencyKey,
        DateTimeOffset removedAtUtc) =>
        new(hostUserId, groupId, memberUserId, memberName, idempotencyKey, removedAtUtc);

    public long HostUserId { get; private set; }

    public long GroupId { get; private set; }

    public long MemberUserId { get; private set; }

    public string MemberName { get; private set; } = string.Empty;

    public Guid IdempotencyKey { get; private set; }

    public DateTimeOffset RemovedAtUtc { get; private set; }
}