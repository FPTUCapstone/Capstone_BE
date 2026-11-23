using TripMate.Domain.Common;

namespace TripMate.Domain.Entities;

public class GroupPollVote : BaseEntity
{
    private GroupPollVote()
    {
    }

    public GroupPollVote(long messageId, int optionIndex, long userId, DateTimeOffset votedAtUtc)
    {
        MessageId = messageId;
        OptionIndex = optionIndex;
        UserId = userId;
        VotedAtUtc = votedAtUtc;
    }

    public long MessageId { get; private set; }
    public GroupMessage Message { get; private set; } = null!;

    public int OptionIndex { get; private set; }

    public long UserId { get; private set; }
    public User User { get; private set; } = null!;

    public DateTimeOffset VotedAtUtc { get; private set; }
}
