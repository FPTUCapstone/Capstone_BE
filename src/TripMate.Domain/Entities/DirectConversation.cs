using TripMate.Domain.Common;

namespace TripMate.Domain.Entities;

public sealed class DirectConversation : BaseEntity
{
    private DirectConversation() { }

    public DirectConversation(long firstUserId, long secondUserId, DateTimeOffset createdAtUtc)
    {
        if (firstUserId <= 0 || secondUserId <= 0 || firstUserId == secondUserId)
            throw new ArgumentException("Two different positive user IDs are required.");
        LowerUserId = Math.Min(firstUserId, secondUserId);
        UpperUserId = Math.Max(firstUserId, secondUserId);
        CreatedAtUtc = createdAtUtc;
    }

    public long LowerReadThroughMessageId { get; private set; }
    public long UpperReadThroughMessageId { get; private set; }
    public void MarkRead(long userId, long throughMessageId)
    {
        if (userId == LowerUserId) LowerReadThroughMessageId = Math.Max(LowerReadThroughMessageId, throughMessageId);
        else if (userId == UpperUserId) UpperReadThroughMessageId = Math.Max(UpperReadThroughMessageId, throughMessageId);
        else throw new ArgumentException("Reader must be a participant.");
    }

    public long LowerUserId { get; private set; }
    public long UpperUserId { get; private set; }
    public User LowerUser { get; private set; } = null!;
    public User UpperUser { get; private set; } = null!;
    public DateTimeOffset CreatedAtUtc { get; private set; }
}