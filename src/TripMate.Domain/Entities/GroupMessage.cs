using TripMate.Domain.Common;
using TripMate.Domain.Enums;

namespace TripMate.Domain.Entities;

public class GroupMessage : BaseEntity
{
    public const string TextType = "text";
    public const string PollType = "poll";
    public const string BroadcastType = "broadcast";
    public const string SplitBillType = "split_bill";
    public const string RollCallType = "roll_call";
    public long Revision { get; private set; }
    public long InteractionRevision { get; private set; }
    public DateTimeOffset? EditedAtUtc { get; private set; }
    public bool FinanciallyConfirmed { get; private set; }
    public string? OriginalContent { get; private set; }
    public string? OriginalMetadataJson { get; private set; }

    public void TouchInteraction() => InteractionRevision++;

    public bool TryEdit(long actorId, string content, string? metadataJson, long expectedRevision,
        DateTimeOffset editedAtUtc, bool unsafePollChange, bool unsafeBroadcastChange = true)
    {
        if (actorId != SenderUserId || Revision != expectedRevision || string.IsNullOrWhiteSpace(content)
            || content.Trim().Length > 4000 || MessageType is not (PollType or BroadcastType or SplitBillType)) return false;
        if (MessageType == PollType && _pollVotes.Count > 0 && unsafePollChange) return false;
        if (MessageType == BroadcastType && _readReceipts.Count > 0 && unsafeBroadcastChange) return false;
        if (MessageType == SplitBillType && (FinanciallyConfirmed || _splitBillPayments.Any(p => p.IsPaid && p.UserId != SenderUserId))) return false;
        if (OriginalContent is null) { OriginalContent = Content; OriginalMetadataJson = MetadataJson; }
        Content = content.Trim(); MetadataJson = metadataJson; EditedAtUtc = editedAtUtc; Revision++;
        return true;
    }

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
        DateTimeOffset createdAtUtc,
        Guid? clientMessageId = null)
    {
        if (groupId <= 0) throw new ArgumentOutOfRangeException(nameof(groupId));
        if (senderUserId <= 0) throw new ArgumentOutOfRangeException(nameof(senderUserId));
        if (string.IsNullOrWhiteSpace(content)) throw new ArgumentException("Content is required.", nameof(content));

        if (clientMessageId == Guid.Empty) throw new ArgumentException("Client message ID must not be empty.", nameof(clientMessageId));
        ClientMessageId = clientMessageId;
        GroupId = groupId;
        SenderUserId = senderUserId;
        MessageType = messageType;
        Content = content;
        OriginalContent = content;
        OriginalMetadataJson = metadataJson;
        MetadataJson = metadataJson;
        CreatedAtUtc = createdAtUtc;
    }

    public Guid? ClientMessageId { get; private set; }

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
        _readReceipts.Add(new GroupMessageReadReceipt(this, userId, readAtUtc));
        TouchInteraction();
    }

    public void AddRollCallCheckin(long userId, DateTimeOffset checkedInAtUtc)
    {
        if (_rollCallCheckins.Any(r => r.UserId == userId)) return;
        _rollCallCheckins.Add(new GroupRollCallCheckin(this, userId, checkedInAtUtc));
    }

    public void UpdateSplitBillPayment(long userId, bool isPaid, DateTimeOffset? paidAtUtc, bool isInitialCreatorMarker = false)
    {
        if (!isInitialCreatorMarker)
        {
            if (isPaid) FinanciallyConfirmed = true;
            TouchInteraction();
        }
        var payment = _splitBillPayments.FirstOrDefault(p => p.UserId == userId);
        if (payment is null)
        {
            _splitBillPayments.Add(new GroupSplitBillPayment(this, userId, isPaid, paidAtUtc));
        }
        else
        {
            payment.UpdateStatus(isPaid, paidAtUtc);
        }
    }

    public void RecordPollVote(long userId, int optionIndex, DateTimeOffset votedAtUtc)
    {
        TouchInteraction();
        var existing = _pollVotes.FirstOrDefault(v => v.UserId == userId);
        if (existing is not null)
        {
            existing.UpdateOption(optionIndex, votedAtUtc);
            return;
        }

        _pollVotes.Add(new GroupPollVote(this, optionIndex, userId, votedAtUtc));
    }
}