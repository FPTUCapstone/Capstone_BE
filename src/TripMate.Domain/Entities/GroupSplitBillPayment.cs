using TripMate.Domain.Common;

namespace TripMate.Domain.Entities;

public class GroupSplitBillPayment : BaseEntity
{
    private GroupSplitBillPayment()
    {
    }

    public GroupSplitBillPayment(long messageId, long userId, bool isPaid, DateTimeOffset? paidAtUtc)
    {
        MessageId = messageId;
        UserId = userId;
        IsPaid = isPaid;
        PaidAtUtc = paidAtUtc;
    }

    public long MessageId { get; private set; }
    public GroupMessage Message { get; private set; } = null!;

    public long UserId { get; private set; }
    public User User { get; private set; } = null!;

    public bool IsPaid { get; private set; }
    public DateTimeOffset? PaidAtUtc { get; private set; }

    public void UpdateStatus(bool isPaid, DateTimeOffset? paidAtUtc)
    {
        IsPaid = isPaid;
        PaidAtUtc = isPaid ? (paidAtUtc ?? DateTimeOffset.UtcNow) : null;
    }
}