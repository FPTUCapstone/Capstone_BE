using System.Globalization;
using System.Security.Claims;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Features.DirectChat;
using TripMate.Domain.Enums;

namespace TripMate.Api.Realtime;

[Authorize]
public sealed class DirectChatHub(IApplicationDbContext db) : Hub
{
    public const string Route = "/hubs/direct-chat";

    public async Task JoinDirectChat(long id)
    {
        if (id <= 0 || !long.TryParse(Context.User?.FindFirstValue(ClaimTypes.NameIdentifier), out var actor)
            || !await db.DirectConversations.AsNoTracking().AnyAsync(c => c.Id == id
                && (c.LowerUserId == actor || c.UpperUserId == actor)
                && c.LowerUser.Status == AccountStatus.Active && c.UpperUser.Status == AccountStatus.Active,
                Context.ConnectionAborted))
            throw new HubException("Active participation is required.");
        // Delivery uses the default authenticated NameIdentifier user channel, never a joined room.
    }
}

public sealed class SignalRDirectChatNotifier(IApplicationDbContext db, IHubContext<DirectChatHub> hub,
    ILogger<SignalRDirectChatNotifier> logger) : IDirectChatNotifier
{
    public async Task NotifyAsync(long conversationId, bool messageReceived, CancellationToken cancellationToken)
    {
        try
        {
            var pair = await db.DirectConversations.AsNoTracking()
                .Where(c => c.Id == conversationId && c.LowerUser.Status == AccountStatus.Active && c.UpperUser.Status == AccountStatus.Active)
                .Select(c => new { c.LowerUserId, c.UpperUserId }).SingleOrDefaultAsync(cancellationToken);
            if (pair is null) return;
            var clients = hub.Clients.Users(new[] { pair.LowerUserId.ToString(CultureInfo.InvariantCulture), pair.UpperUserId.ToString(CultureInfo.InvariantCulture) });
            var change = new DirectChatChanged(conversationId);
            if (messageReceived) await clients.SendAsync("MessageReceived", change, cancellationToken);
            await clients.SendAsync("ConversationChanged", change, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Persistence has succeeded; transport failure must not turn a durable send into HTTP failure.
            logger.LogWarning(exception, "Direct chat notification failed for conversation {ConversationId}", conversationId);
        }
    }
}