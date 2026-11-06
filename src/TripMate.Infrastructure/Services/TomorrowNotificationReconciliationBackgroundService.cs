using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Features.Weather.Emergency;
using TripMate.Infrastructure.Weather;

namespace TripMate.Infrastructure.Services;

public sealed class TomorrowNotificationReconciliationBackgroundService(
    IServiceScopeFactory scopeFactory,
    IOptions<TomorrowIoOptions> options,
    TimeProvider timeProvider,
    ILogger<TomorrowNotificationReconciliationBackgroundService> logger) : BackgroundService
{
    private const int MaxPagesPerRun = 10;
    private DateTimeOffset? _lastCheckpointUtc;

    public async Task RunReconciliationCycleAsync(CancellationToken cancellationToken)
    {
        if (!options.Value.Enabled)
        {
            logger.LogDebug("Tomorrow.io alert processing is disabled. Skipping notification reconciliation.");
            return;
        }

        if (string.IsNullOrWhiteSpace(options.Value.ApiKey))
        {
            logger.LogWarning("Tomorrow.io ApiKey is not set. Skipping notification reconciliation.");
            return;
        }

        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var alertsClient = scope.ServiceProvider.GetRequiredService<ITomorrowAlertsClient>();
            var inboxStore = scope.ServiceProvider.GetRequiredService<IWeatherAlertInboxStore>();

            var now = timeProvider.GetUtcNow();
            var lookback = TimeSpan.FromMinutes(Math.Max(1, options.Value.ReconciliationLookbackMinutes));
            var startTime = _lastCheckpointUtc.HasValue && _lastCheckpointUtc.Value > now.Subtract(lookback)
                ? _lastCheckpointUtc.Value.Subtract(TimeSpan.FromMinutes(5)) // 5-minute overlap for safety
                : now.Subtract(lookback);
            var endTime = now;

            logger.LogInformation("Starting Tomorrow.io notification reconciliation sweep from {Start} to {End}.",
                startTime, endTime);

            string? pageToken = null;
            int pages = 0;
            int enqueuedCount = 0;

            while (pages < MaxPagesPerRun && !cancellationToken.IsCancellationRequested)
            {
                var response = await alertsClient.GetNotificationsAsync(startTime, endTime, pageToken, cancellationToken);
                if (response.ProviderUnavailable)
                {
                    // Keep the checkpoint so the next sweep covers this window again.
                    return;
                }

                if (response.Notifications.Count == 0)
                {
                    break;
                }

                foreach (var notif in response.Notifications.Where(item => item.AlertId == options.Value.AlertId))
                {
                    bool enqueued = await inboxStore.EnqueueIfNotExistsAsync(
                        provider: WeatherProviders.TomorrowIo,
                        providerNotificationId: notif.Id,
                        payload: notif.RawJson,
                        cancellationToken: cancellationToken);

                    if (enqueued) enqueuedCount++;
                }

                pages++;
                if (string.IsNullOrWhiteSpace(response.NextPageToken))
                {
                    break;
                }

                pageToken = response.NextPageToken;
            }

            _lastCheckpointUtc = endTime;
            logger.LogInformation("Completed Tomorrow.io notification reconciliation sweep. Pages: {Pages}, Enqueued: {EnqueuedCount}.",
                pages, enqueuedCount);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Normal shutdown
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error occurred during Tomorrow.io notification reconciliation.");
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var interval = TimeSpan.FromMinutes(Math.Max(1, options.Value.ReconciliationIntervalMinutes));

            await RunReconciliationCycleAsync(stoppingToken);

            try
            {
                await Task.Delay(interval, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }
}