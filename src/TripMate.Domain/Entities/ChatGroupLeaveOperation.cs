using TripMate.Domain.Common;
namespace TripMate.Domain.Entities;

public sealed class ChatGroupLeaveOperation : BaseEntity
{
    public const string RetryIndex = "UX_ChatGroupLeaveOperations_ClientKey";
    public const string LeftStatus = "Left";
    private ChatGroupLeaveOperation() { }
    public ChatGroupLeaveOperation(ChatGroup group, long actorId, Guid clientRequestId,
        long? replacementHostUserId, DateTimeOffset leftAtUtc)
    {
        if (actorId <= 0 || clientRequestId == Guid.Empty || group.HostUserId == actorId)
            throw new ArgumentException("A valid leave receipt with a surviving host is required.");
        Group = group; ActorId = actorId; ClientRequestId = clientRequestId;
        ReplacementHostUserId = replacementHostUserId; NewHostUserId = group.HostUserId;
        LeftAtUtc = leftAtUtc.ToUniversalTime();
    }
    public long GroupId { get; private set; }
    public ChatGroup Group { get; private set; } = null!;
    public long ActorId { get; private set; }
    public Guid ClientRequestId { get; private set; }
    public long? ReplacementHostUserId { get; private set; }
    public long NewHostUserId { get; private set; }
    public DateTimeOffset LeftAtUtc { get; private set; }
}