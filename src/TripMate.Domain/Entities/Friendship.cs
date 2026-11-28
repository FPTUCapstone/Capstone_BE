using TripMate.Domain.Common;

namespace TripMate.Domain.Entities;

public enum FriendshipTransition { Applied, Delete, Forbidden, Conflict, Invalid }

public sealed class Friendship : BaseEntity
{
    public const string Pending = "Pending";
    public const string Accepted = "Accepted";
    public const string Request = "request";
    public const string Accept = "accept";
    public const string Decline = "decline";
    public const string Cancel = "cancel";
    public const string Remove = "remove";

    private Friendship() { }

    public Friendship(long requesterId, long recipientId, DateTimeOffset createdAtUtc)
    {
        if (requesterId <= 0 || recipientId <= 0 || requesterId == recipientId)
            throw new ArgumentException("Two different positive user IDs are required.");
        LowerUserId = Math.Min(requesterId, recipientId);
        UpperUserId = Math.Max(requesterId, recipientId);
        RequestedByUserId = requesterId;
        CreatedAtUtc = createdAtUtc.ToUniversalTime();
    }

    public long LowerUserId { get; private set; }
    public long UpperUserId { get; private set; }
    public long RequestedByUserId { get; private set; }
    public string Status { get; private set; } = Pending;
    public DateTimeOffset CreatedAtUtc { get; private set; }
    public User LowerUser { get; private set; } = null!;
    public User UpperUser { get; private set; } = null!;

    public static bool IsAction(string? action) => action is Request or Accept or Decline or Cancel or Remove;

    public FriendshipTransition Apply(long actorId, string action)
    {
        if (actorId != LowerUserId && actorId != UpperUserId) return FriendshipTransition.Forbidden;
        if (!IsAction(action)) return FriendshipTransition.Invalid;
        // Crossed requests preserve the original requester and require explicit recipient consent.
        if (action == Request) return FriendshipTransition.Applied;
        if ((action is Accept or Decline && actorId == RequestedByUserId)
            || (action == Cancel && actorId != RequestedByUserId)) return FriendshipTransition.Forbidden;
        if (action == Accept)
        {
            Status = Accepted;
            return FriendshipTransition.Applied;
        }
        if (action == Remove ? Status != Accepted : Status != Pending) return FriendshipTransition.Conflict;
        return FriendshipTransition.Delete;
    }
}