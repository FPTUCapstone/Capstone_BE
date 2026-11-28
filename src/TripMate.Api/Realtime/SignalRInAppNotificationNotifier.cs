using System.Globalization;

using Microsoft.AspNetCore.SignalR;

using TripMate.Application.Common.Interfaces;

namespace TripMate.Api.Realtime;

public sealed class SignalRInAppNotificationNotifier(
    IHubContext<NotificationsHub> hubContext,
    ILogger<SignalRInAppNotificationNotifier> logger)
    : IInAppNotificationNotifier
{
    public async Task NotifyInboxChangedAsync(
        long userId,
        CancellationToken cancellationToken)
    {
        if (userId <= 0)
        {
            return;
        }

        try
        {
            await hubContext.Clients
                .User(userId.ToString(CultureInfo.InvariantCulture))
                .SendAsync(
                    NotificationsHub.ClientEvent,
                    new { occurredAtUtc = DateTimeOffset.UtcNow },
                    cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(
                exception,
                "Failed to send real-time inbox refresh hint for user {UserId}.",
                userId);
        }
    }
}

