using System.Globalization;

using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Features.ChatGroups;
using TripMate.Domain.Enums;
namespace TripMate.Api.Realtime;

public sealed class SignalRChatGroupNotifier(IApplicationDbContext db, IHubContext<GroupChatHub> hub,
    ILogger<SignalRChatGroupNotifier> logger) : IChatGroupNotifier
{
    public async Task NotifyMembershipChangedAsync(long groupId, long departingActorId, CancellationToken cancellationToken)
    {
        try
        {
            var recipients = await db.ChatGroupMembers.AsNoTracking()
                .Where(m => m.GroupId == groupId && m.User.Status == AccountStatus.Active)
                .Select(m => m.UserId).ToListAsync(cancellationToken);
            recipients.Add(departingActorId);
            var clients = hub.Clients.Users(recipients.Distinct().Select(id => id.ToString(CultureInfo.InvariantCulture)).ToArray());
            var change = new ChatGroupChanged(groupId);
            await clients.SendAsync("ConversationChanged", change, cancellationToken);
            await clients.SendAsync("MembershipChanged", change, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "Chat group membership notification failed for group {GroupId}", groupId);
        }
    }

    public async Task NotifyAsync(long groupId, bool messageReceived, CancellationToken cancellationToken)
    {
        try
        {
            var recipients = await db.ChatGroupMembers.AsNoTracking()
                .Where(m => m.GroupId == groupId && m.User.Status == AccountStatus.Active)
                .Select(m => m.UserId).ToListAsync(cancellationToken);
            if (recipients.Count == 0) return;
            var clients = hub.Clients.Users(recipients.Select(id => id.ToString(CultureInfo.InvariantCulture)).ToArray());
            var change = new ChatGroupChanged(groupId);
            if (messageReceived) await clients.SendAsync("MessageReceived", change, cancellationToken);
            await clients.SendAsync("ConversationChanged", change, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Persistence has succeeded; transport failure must not turn a durable send into HTTP failure.
            logger.LogWarning(exception, "Chat group notification failed for group {GroupId}", groupId);
        }
    }
}