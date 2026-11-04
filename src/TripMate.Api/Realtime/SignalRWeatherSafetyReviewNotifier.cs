using System.Globalization;
using Microsoft.AspNetCore.SignalR;
using TripMate.Application.Common.Interfaces;

namespace TripMate.Api.Realtime;

public sealed class SignalRWeatherSafetyReviewNotifier(
    IHubContext<WeatherSafetyReviewHub> hubContext,
    ILogger<SignalRWeatherSafetyReviewNotifier> logger)
    : IWeatherSafetyReviewNotifier
{
    public async Task NotifyChangedAsync(
        long operatorUserId,
        WeatherSafetyReviewChanged notification,
        CancellationToken cancellationToken)
    {
        try
        {
            await hubContext.Clients
                .User(operatorUserId.ToString(CultureInfo.InvariantCulture))
                .SendAsync(
                    WeatherSafetyReviewHub.ClientEvent,
                    notification,
                    cancellationToken);
        }
        catch (Exception exception)
        {
            logger.LogWarning(
                exception,
                "Failed to send weather safety review refresh hint for review {ReviewId}.",
                notification.ReviewId);
        }
    }
}
