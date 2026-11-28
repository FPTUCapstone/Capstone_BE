using MediatR;

using TripMate.Application.Common.Models;

namespace TripMate.Application.Features.Friendships;

public static class FriendshipErrors
{
    public const string Invalid = "Friendships.Invalid";
    public const string Forbidden = "Friendships.Forbidden";
    public const string NotFound = "Friendships.NotFound";
    public const string Conflict = "Friendships.Conflict";
}

public sealed record FriendshipDto(long UserId, string DisplayName, string? AvatarUrl, string Status, long RequestedByUserId);
public sealed record ListFriendships(long ActorId) : IRequest<Result<IReadOnlyList<FriendshipDto>>>;
public sealed record UpdateFriendship(long ActorId, long OtherUserId, string? Action) : IRequest<Result>;