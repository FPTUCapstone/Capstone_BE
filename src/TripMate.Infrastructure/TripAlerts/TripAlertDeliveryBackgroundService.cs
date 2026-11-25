using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace TripMate.Infrastructure.TripAlerts;

internal sealed class TripAlertDeliveryBackgroundService(
    IServiceScopeFactory scopeFactory,
    ILogger<TripAlertDeliveryBackgroundService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var foundWork = false;
            try
            {
                await using AsyncServiceScope scope = scopeFactory.CreateAsyncScope();
                var queue = scope.ServiceProvider.GetRequiredService<AlertDeliveryQueue>();
                var sender = scope.ServiceProvider.GetRequiredService<FirebaseTripAlertPushSender>();
                ClaimedTripAlert? alert = await queue.ClaimNextAsync(stoppingToken);
                foundWork = alert is not null;
                if (alert is not null)
                {
                    var result = new TripAlertPushResult(false, []);
                    try
                    {
                        result = await sender.SendAsync(alert, stoppingToken);
                    }
                    catch (Exception exception) when (exception is not OperationCanceledException)
                    {
                        logger.LogWarning("Trip alert push {NotificationId} failed with {ExceptionType}.",
                            alert.NotificationId, exception.GetType().Name);
                    }

                    await queue.CompleteAsync(alert, result, stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogError("Trip alert delivery sweep failed with {ExceptionType}.", exception.GetType().Name);
            }

            if (!foundWork)
            {
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
            }
        }
    }
}