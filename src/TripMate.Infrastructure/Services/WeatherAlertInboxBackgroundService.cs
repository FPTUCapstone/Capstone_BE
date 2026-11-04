using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Features.Weather.Processing;
using TripMate.Infrastructure.Weather;

namespace TripMate.Infrastructure.Services;

public sealed class WeatherAlertInboxBackgroundService(
    IServiceScopeFactory scopeFactory,
    IOptions<TomorrowIoOptions> options,
    ILogger<WeatherAlertInboxBackgroundService> logger) : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan LeaseDuration = TimeSpan.FromSeconds(30);
    private readonly string _workerId = $"worker-{Guid.NewGuid():N}"[..16];

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            if (!options.Value.Enabled)
            {
                try { await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken); }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
                continue;
            }

            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var inboxStore = scope.ServiceProvider.GetRequiredService<IWeatherAlertInboxStore>();
                var processor = scope.ServiceProvider.GetRequiredService<IWeatherAlertInboxProcessor>();

                var items = await inboxStore.LeasePendingItemsAsync(
                    batchSize: 10,
                    leaseDuration: LeaseDuration,
                    leasedBy: _workerId,
                    cancellationToken: stoppingToken);

                foreach (var item in items)
                {
                    try
                    {
                        await processor.ProcessInboxItemAsync(item, stoppingToken);
                    }
                    catch (Exception ex)
                    {
                        logger.LogError(ex, "Failed to process inbox item {InboxId}.", item.Id);
                        await inboxStore.MarkFailedAsync(item.Id, ex.Message, stoppingToken);
                    }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error occurred in weather alert inbox background sweep.");
            }

            try { await Task.Delay(PollInterval, stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
        }
    }
}
