using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace TripMate.Infrastructure.Services;

internal sealed class UserAdministrationNotificationDeliveryBackgroundService(
    IServiceScopeFactory scopeFactory,
    IOptions<UserAdministrationNotificationDeliveryOptions> options,
    ILogger<UserAdministrationNotificationDeliveryBackgroundService> logger)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        UserAdministrationNotificationDeliveryOptions settings = options.Value;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using AsyncServiceScope scope = scopeFactory.CreateAsyncScope();
                var processor = scope.ServiceProvider
                    .GetRequiredService<UserAdministrationNotificationDeliveryProcessor>();
                await processor.ProcessDueBatchAsync(
                    settings.LeaseDuration,
                    settings.BatchSize,
                    stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogError(
                    exception,
                    "Administrative notification delivery batch failed with {ExceptionType}.",
                    exception.GetType().Name);
            }

            try
            {
                await Task.Delay(settings.PollInterval, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }
}