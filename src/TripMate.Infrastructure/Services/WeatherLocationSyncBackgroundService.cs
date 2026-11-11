using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using TripMate.Application.Features.Weather.Locations;
using TripMate.Infrastructure.Weather;

namespace TripMate.Infrastructure.Services;

public sealed class WeatherLocationSyncBackgroundService(
    IServiceScopeFactory scopeFactory,
    IOptions<TomorrowIoOptions> options,
    ILogger<WeatherLocationSyncBackgroundService> logger) : BackgroundService
{
    public async Task RunSyncCycleAsync(CancellationToken cancellationToken)
    {
        if (!options.Value.Enabled)
        {
            logger.LogDebug("Tomorrow.io alert processing is disabled. Skipping weather location sync.");
            return;
        }

        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var syncService = scope.ServiceProvider.GetRequiredService<IWeatherLocationSyncService>();

            logger.LogInformation("Starting periodic weather location synchronization.");
            await syncService.SyncUpcomingLocationsAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Graceful shutdown
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error occurred during weather location synchronization.");
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var interval = TimeSpan.FromHours(Math.Max(1, options.Value.LocationSyncIntervalHours));

            await RunSyncCycleAsync(stoppingToken);

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