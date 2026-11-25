using TripMate.Domain.Common;

namespace TripMate.Domain.Entities;

public sealed class DirectMessage : BaseEntity
{
    public const int MaxContentLength = 4000;
    private DirectMessage() { }

    public DirectMessage(DirectConversation conversation, long senderUserId, string content,
        Guid clientMessageId, DateTimeOffset createdAtUtc)
    {
        ArgumentNullException.ThrowIfNull(conversation);
        if (senderUserId != conversation.LowerUserId && senderUserId != conversation.UpperUserId)
            throw new ArgumentException("Sender must be a participant.");
        if (string.IsNullOrWhiteSpace(content) || content.Trim().Length > MaxContentLength || clientMessageId == Guid.Empty)
            throw new ArgumentException("Valid content and client message ID are required.");
        Conversation = conversation;
        SenderUserId = senderUserId;
        Content = content.Trim();
        ClientMessageId = clientMessageId;
        CreatedAtUtc = createdAtUtc;
    }

    public long ConversationId { get; private set; }
    public DirectConversation Conversation { get; private set; } = null!;
    public long SenderUserId { get; private set; }
    public User SenderUser { get; private set; } = null!;
    public string Content { get; private set; } = string.Empty;
    public Guid ClientMessageId { get; private set; }
    public DateTimeOffset CreatedAtUtc { get; private set; }
}