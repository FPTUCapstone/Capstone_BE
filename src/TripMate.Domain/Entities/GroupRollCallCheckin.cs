using TripMate.Domain.Common;

namespace TripMate.Domain.Entities;

public class GroupRollCallCheckin : BaseEntity
{
    private GroupRollCallCheckin()
    {
    }

    public GroupRollCallCheckin(long messageId, long userId, DateTimeOffset checkedInAtUtc)
    {
        MessageId = messageId;
        UserId = userId;
        CheckedInAtUtc = checkedInAtUtc;
    }

    public long MessageId { get; private set; }
    public GroupMessage Message { get; private set; } = null!;

    public long UserId { get; private set; }
    public User User { get; private set; } = null!;

    public DateTimeOffset CheckedInAtUtc { get; private set; }
}
