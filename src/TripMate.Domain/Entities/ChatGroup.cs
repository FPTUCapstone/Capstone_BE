using TripMate.Domain.Common;
namespace TripMate.Domain.Entities;

public sealed class ChatGroup : BaseEntity
{
    public const int MaxNameLength = 100;
    public const int MaxMembers = 50;
    private ChatGroup() { }
    public ChatGroup(long hostUserId, string name, IEnumerable<long> memberUserIds, Guid clientRequestId, DateTimeOffset createdAtUtc)
    {
        var ids = memberUserIds.Append(hostUserId).Distinct().Order().ToArray();
        if (hostUserId <= 0 || ids.Any(i => i <= 0) || ids.Length is < 3 or > MaxMembers
            || string.IsNullOrWhiteSpace(name) || name.Trim().Length > MaxNameLength || clientRequestId == Guid.Empty)
            throw new ArgumentException("Valid group name, creator, request key and 3..50 members are required.");
        HostUserId = hostUserId;
        CreatedByUserId = hostUserId;
        InitialMemberUserIds = string.Join(",", ids);
        GroupName = name.Trim();
        InitialGroupName = GroupName;
        ClientRequestId = clientRequestId;
        CreatedAtUtc = createdAtUtc;
        Members = ids.Select(id => new ChatGroupMember(this, id)).ToList();
    }
    public void TransferHost(long actorId, long replacementUserId)
    {
        if (IsClosed || HostUserId != actorId || replacementUserId == actorId || !Members.Any(m => m.UserId == replacementUserId))
            throw new ArgumentException("An existing other member must replace the current host.");
        HostUserId = replacementUserId;
    }
    public void Rename(long actorId, string name)
    {
        if (IsClosed || HostUserId != actorId || string.IsNullOrWhiteSpace(name) || name.Trim().Length > MaxNameLength)
            throw new ArgumentException("Only the host of an open group may set a valid name.");
        GroupName = name.Trim();
    }
    public ChatGroupMember? RemoveMember(long actorId, long userId)
    {
        if (IsClosed || HostUserId != actorId || userId <= 0 || userId == actorId || userId == HostUserId)
            throw new ArgumentException("Only the host of an open group may remove another member.");
        var member = Members.SingleOrDefault(m => m.UserId == userId);
        if (member is not null) Members = Members.Where(m => m.UserId != userId).ToList();
        return member;
    }
    public ChatGroupMember Leave(long actorId)
    {
        if (IsClosed || HostUserId == actorId || !Members.Any(m => m.UserId == actorId))
            throw new ArgumentException("A member may leave an open group after transferring host if necessary.");
        var member = Members.Single(m => m.UserId == actorId);
        Members = Members.Where(m => m.UserId != actorId).ToList();
        return member;
    }
    public void Dissolve(long actorId)
    {
        if (HostUserId != actorId) throw new ArgumentException("Only the host may dissolve a group.");
        IsClosed = true;
    }
    public string GroupName { get; private set; } = string.Empty;
    public string InitialGroupName { get; private set; } = string.Empty;
    public bool IsClosed { get; private set; }
    public long HostUserId { get; private set; }
    public long CreatedByUserId { get; private set; }
    public string InitialMemberUserIds { get; private set; } = string.Empty;
    public User HostUser { get; private set; } = null!;
    public Guid ClientRequestId { get; private set; }
    public DateTimeOffset CreatedAtUtc { get; private set; }
    public IReadOnlyCollection<ChatGroupMember> Members { get; private set; } = new List<ChatGroupMember>();
}

public sealed class ChatGroupMember : BaseEntity
{
    private ChatGroupMember() { }
    internal ChatGroupMember(ChatGroup group, long userId) { Group = group; UserId = userId; }
    public long GroupId { get; private set; }
    public ChatGroup Group { get; private set; } = null!;
    public long UserId { get; private set; }
    public User User { get; private set; } = null!;
    public long ReadThroughMessageId { get; private set; }
    public void MarkRead(long messageId)
    {
        if (messageId <= 0) throw new ArgumentException("Positive message ID required.");
        ReadThroughMessageId = Math.Max(ReadThroughMessageId, messageId);
    }
}

public sealed class ChatGroupMessage : BaseEntity
{
    public const int MaxContentLength = 4000;
    private ChatGroupMessage() { }
    public ChatGroupMessage(ChatGroup group, long senderUserId, string content, Guid clientMessageId, DateTimeOffset createdAtUtc)
    {
        if (group.IsClosed || !group.Members.Any(m => m.UserId == senderUserId) || string.IsNullOrWhiteSpace(content)
            || content.Trim().Length > MaxContentLength || clientMessageId == Guid.Empty)
            throw new ArgumentException("Member, valid text and client message ID are required.");
        Group = group; SenderUserId = senderUserId; Content = content.Trim();
        ClientMessageId = clientMessageId; CreatedAtUtc = createdAtUtc;
    }
    public long GroupId { get; private set; }
    public ChatGroup Group { get; private set; } = null!;
    public long SenderUserId { get; private set; }
    public User SenderUser { get; private set; } = null!;
    public string Content { get; private set; } = string.Empty;
    public Guid ClientMessageId { get; private set; }
    public DateTimeOffset CreatedAtUtc { get; private set; }
}