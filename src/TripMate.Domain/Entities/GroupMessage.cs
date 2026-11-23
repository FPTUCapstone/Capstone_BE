using TripMate.Domain.Common;
using TripMate.Domain.Enums;

namespace TripMate.Domain.Entities;

public class GroupMessage : BaseEntity
{
    private readonly List<GroupMessageReadReceipt> _readReceipts = [];
    private readonly List<GroupRollCallCheckin> _rollCallCheckins = [];
    private readonly List<GroupSplitBillPayment> _splitBillPayments = [];
    private readonly List<GroupPollVote> _pollVotes = [];

    private GroupMessage()
    {
    }

    public GroupMessage(
        long groupId,
        long senderUserId,
        string messageType,
        string content,
        string? metadataJson,
        DateTimeOffset createdAtUtc)
    {
        if (groupId <= 0) throw new ArgumentOutOfRangeException(nameof(groupId));
        if (senderUserId <= 0) throw new ArgumentOutOfRangeException(nameof(senderUserId));
        if (string.IsNullOrWhiteSpace(content)) throw new ArgumentException("Content is required.", nameof(content));

        GroupId = groupId;
        SenderUserId = senderUserId;
        MessageType = messageType;
        Content = content;
        MetadataJson = metadataJson;
        CreatedAtUtc = createdAtUtc;
    }

    public long GroupId { get; private set; }
    public TravelGroup Group { get; private set; } = null!;

    public long SenderUserId { get; private set; }
    public User SenderUser { get; private set; } = null!;

    public string MessageType { get; private set; } = string.Empty;
    public string Content { get; private set; } = string.Empty;
    public string? MetadataJson { get; private set; }
    public DateTimeOffset CreatedAtUtc { get; private set; }

    public IReadOnlyCollection<GroupMessageReadReceipt> ReadReceipts => _readReceipts.AsReadOnly();
    public IReadOnlyCollection<GroupRollCallCheckin> RollCallCheckins => _rollCallCheckins.AsReadOnly();
    public IReadOnlyCollection<GroupSplitBillPayment> SplitBillPayments => _splitBillPayments.AsReadOnly();
    public IReadOnlyCollection<GroupPollVote> PollVotes => _pollVotes.AsReadOnly();

    public void AddReadReceipt(long userId, DateTimeOffset readAtUtc)
    {
        if (_readReceipts.Any(r => r.UserId == userId)) return;
        _readReceipts.Add(new GroupMessageReadReceipt(Id, userId, readAtUtc));
    }

    public void AddRollCallCheckin(long userId, DateTimeOffset checkedInAtUtc)
    {
        if (_rollCallCheckins.Any(r => r.UserId == userId)) return;
        _rollCallCheckins.Add(new GroupRollCallCheckin(Id, userId, checkedInAtUtc));
    }

    public void UpdateSplitBillPayment(long userId, bool isPaid, DateTimeOffset? paidAtUtc)
    {
        var payment = _splitBillPayments.FirstOrDefault(p => p.UserId == userId);
        if (payment is null)
        {
            _splitBillPayments.Add(new GroupSplitBillPayment(Id, userId, isPaid, paidAtUtc));
        }
        else
        {
            payment.UpdateStatus(isPaid, paidAtUtc);
        }
    }

    public void RecordPollVote(long userId, int optionIndex, DateTimeOffset votedAtUtc)
    {
        var existing = _pollVotes.FirstOrDefault(v => v.UserId == userId);
        if (existing is not null)
        {
            _pollVotes.Remove(existing);
        }
        _pollVotes.Add(new GroupPollVote(Id, optionIndex, userId, votedAtUtc));
    }
}
