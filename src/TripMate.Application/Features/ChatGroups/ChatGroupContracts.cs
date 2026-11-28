using MediatR;

using TripMate.Application.Common.Models;
namespace TripMate.Application.Features.ChatGroups;

public static class ChatGroupErrors
{
    public const string Invalid = "ChatGroups.Invalid";
    public const string Forbidden = "ChatGroups.Forbidden";
    public const string NotFound = "ChatGroups.NotFound";
    public const string Conflict = "ChatGroups.Conflict";
}
public sealed record ChatGroupConversationDto(long GroupId, string GroupName, int MemberCount, long HostUserId,
    string? LastMessage, DateTimeOffset? LastMessageAtUtc, int UnreadCount, bool IsClosed = false);
public sealed record ChatGroupPage(IReadOnlyList<ChatGroupConversationDto> Items, long? NextBeforeGroupId);
public sealed record ChatGroupMessageDto(long MessageId, long GroupId, long SenderUserId, string SenderName,
    string Content, DateTimeOffset CreatedAtUtc, string ClientMessageId);
public sealed record CreateChatGroup(long ActorId, string? Name, long[]? MemberUserIds, Guid ClientRequestId) : IRequest<Result<ChatGroupConversationDto>>;
public sealed record ListChatGroups(long ActorId, int Limit = 50, long? BeforeGroupId = null) : IRequest<Result<ChatGroupPage>>;
public sealed record GetChatGroupMessages(long ActorId, long GroupId, int Limit = 50, long? BeforeMessageId = null) : IRequest<Result<IReadOnlyList<ChatGroupMessageDto>>>;
public sealed record SendChatGroupMessage(long ActorId, long GroupId, string? Content, Guid ClientMessageId) : IRequest<Result<ChatGroupMessageDto>>;
public sealed record ReadChatGroup(long ActorId, long GroupId, long ThroughMessageId) : IRequest<Result>;
// Invalidation hints only; HTTP authorization remains authoritative.
public sealed record ChatGroupChanged(long GroupId);
public interface IChatGroupNotifier
{
    Task NotifyMembershipChangedAsync(long groupId, long departingActorId, CancellationToken cancellationToken) =>
        NotifyAsync(groupId, false, cancellationToken);
    Task NotifyAsync(long groupId, bool messageReceived, CancellationToken cancellationToken);
}

public sealed record GetChatGroupMembers(long ActorId, long GroupId) : IRequest<Result<ChatGroupMembersDto>>;
public sealed record ChatGroupMemberDto(long UserId, string DisplayName, string? AvatarUrl, bool IsHost, bool CanReceiveHost = false);
public sealed record ChatGroupMembersDto(long GroupId, string GroupName, int MemberCount, IReadOnlyList<ChatGroupMemberDto> Members, bool IsClosed = false);
public sealed record LeaveChatGroup(long ActorId, long GroupId, Guid IdempotencyKey, long? ReplacementHostUserId) : IRequest<Result<ChatGroupLeaveReceipt>>;
public sealed record ChatGroupLeaveReceipt(long GroupId, string MembershipStatus, long NewHostUserId, DateTimeOffset LeftAtUtc);
public sealed record RenameChatGroup(long ActorId, long GroupId, string? Name) : IRequest<Result<ChatGroupConversationDto>>;
public sealed record KickChatGroupMember(long ActorId, long GroupId, long UserId) : IRequest<Result<bool>>;
public sealed record DissolveChatGroup(long ActorId, long GroupId) : IRequest<Result<bool>>;