using TripMate.Domain.Common;

namespace TripMate.Domain.Entities;

public class GroupMessageReadReceipt : BaseEntity
{
    private GroupMessageReadReceipt()
    {
    }

    public GroupMessageReadReceipt(long messageId, long userId, DateTimeOffset readAtUtc)
    {
        MessageId = messageId;
        UserId = userId;
        ReadAtUtc = readAtUtc;
    }

    public long MessageId { get; private set; }
    public GroupMessage Message { get; private set; } = null!;

    public long UserId { get; private set; }
    public User User { get; private set; } = null!;

    public DateTimeOffset ReadAtUtc { get; private set; }
}
