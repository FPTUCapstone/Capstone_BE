using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using TripMate.Domain.Enums;

namespace TripMate.Api.Realtime;

[Authorize]
public sealed class GroupChatHub : Hub
{
    public const string Route = "/hubs/group-chat";

    public async Task JoinGroupChat(long groupId)
    {
        await Groups.AddToGroupAsync(Context.ConnectionId, GetGroupRoom(groupId));
    }

    public async Task LeaveGroupChat(long groupId)
    {
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, GetGroupRoom(groupId));
    }

    public static string GetGroupRoom(long groupId) => $"travel-group-{groupId}";
}
