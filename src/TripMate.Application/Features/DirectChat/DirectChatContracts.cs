using MediatR;

using TripMate.Application.Common.Models;

namespace TripMate.Application.Features.DirectChat;

public static class DirectChatErrors
{
    public const string Invalid = "DirectChat.Invalid";
    public const string Forbidden = "DirectChat.Forbidden";
    public const string NotFound = "DirectChat.NotFound";
    public const string Conflict = "DirectChat.Conflict";
}

public sealed record DirectUserDto(long UserId, string DisplayName, string? AvatarUrl);
public sealed record DirectConversationDto(long ConversationId, long OtherUserId, string DisplayName,
    string? AvatarUrl, string? LastMessage, DateTimeOffset? LastMessageAtUtc, int UnreadCount);
public sealed record DirectConversationPage(IReadOnlyList<DirectConversationDto> Items, long? NextBeforeConversationId);
public sealed record DirectMessageDto(long MessageId, long ConversationId, long SenderUserId,
    string SenderName, string Content, DateTimeOffset CreatedAtUtc, string ClientMessageId, bool IsReadByOther);
public sealed record SearchDirectUsers(long ActorId, string? Search, int Limit = 20) : IRequest<Result<IReadOnlyList<DirectUserDto>>>;
public sealed record StartDirectConversation(long ActorId, long OtherUserId) : IRequest<Result<DirectConversationDto>>;
public sealed record ListDirectConversations(long ActorId, int Limit = 50, long? BeforeConversationId = null) : IRequest<Result<DirectConversationPage>>;
public sealed record GetDirectMessages(long ActorId, long ConversationId, int Limit = 50, long? BeforeMessageId = null) : IRequest<Result<IReadOnlyList<DirectMessageDto>>>;
public sealed record SendDirectMessage(long ActorId, long ConversationId, string? Content, Guid ClientMessageId) : IRequest<Result<DirectMessageDto>>;
public sealed record ReadDirectConversation(long ActorId, long ConversationId, long ThroughMessageId) : IRequest<Result>;

// Notifications are invalidation hints. Clients fetch the authorized HTTP resources for content.
public sealed record DirectChatChanged(long ConversationId);
public interface IDirectChatNotifier
{
    Task NotifyAsync(long conversationId, bool messageReceived, CancellationToken cancellationToken);
}