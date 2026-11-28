using System.Security.Claims;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

using TripMate.Application.Common.Interfaces;
using TripMate.Domain.Enums;

namespace TripMate.Api.Realtime;

[Authorize]
public sealed class GroupChatHub(IApplicationDbContext dbContext) : Hub
{
    public const string Route = "/hubs/group-chat";

    [Authorize(Roles = $"{nameof(UserRole.Traveler)},{nameof(UserRole.TourOperator)}")]
    public async Task JoinGroupChat(long groupId)
    {
        if (groupId <= 0)
        {
            throw new HubException("A positive groupId is required.");
        }

        var userIdValue = Context.User?.FindFirstValue(ClaimTypes.NameIdentifier);
        if (!long.TryParse(userIdValue, out var userId) || userId <= 0)
        {
            throw new HubException("Authenticated user identity is required.");
        }

        var isActiveMember = await dbContext.GroupMembers
            .AsNoTracking()
            .AnyAsync(
                member => member.GroupId == groupId
                    && member.UserId == userId
                    && member.Status == GroupMemberStatus.Active
                    && member.TravelGroup.Status == TravelGroupStatus.Active,
                Context.ConnectionAborted);

        if (!isActiveMember)
        {
            throw new HubException("Active group membership is required to join this group chat.");
        }

        await Groups.AddToGroupAsync(Context.ConnectionId, GetGroupRoom(groupId), Context.ConnectionAborted);
    }

    [Authorize(Roles = $"{nameof(UserRole.Traveler)},{nameof(UserRole.TourOperator)}")]
    public async Task LeaveGroupChat(long groupId)
    {
        if (groupId <= 0)
        {
            return;
        }

        await Groups.RemoveFromGroupAsync(Context.ConnectionId, GetGroupRoom(groupId), Context.ConnectionAborted);
    }

    public static string GetGroupRoom(long groupId) => $"travel-group-{groupId}";
}