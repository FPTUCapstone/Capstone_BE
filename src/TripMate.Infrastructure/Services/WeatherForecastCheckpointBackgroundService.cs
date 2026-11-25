using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using TripMate.Application.Features.Weather.Processing;
using TripMate.Infrastructure.Weather;

namespace TripMate.Infrastructure.Services;

public sealed class WeatherForecastCheckpointBackgroundService(
    IServiceScopeFactory scopeFactory,
    IOptions<TomorrowIoOptions> options,
    ILogger<WeatherForecastCheckpointBackgroundService> logger) : BackgroundService
{
    public async Task RunCheckpointCycleAsync(CancellationToken cancellationToken)
    {
        if (!options.Value.Enabled)
        {
            logger.LogDebug("Tomorrow.io alert processing is disabled. Skipping forecast checkpoint re-evaluation.");
            return;
        }

        if (string.IsNullOrWhiteSpace(options.Value.ApiKey))
        {
            logger.LogWarning("Tomorrow.io ApiKey is not set. Skipping forecast checkpoint re-evaluation.");
            return;
        }

        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var checkpointService = scope.ServiceProvider.GetRequiredService<IWeatherForecastCheckpointService>();

            int updated = await checkpointService.EvaluateDueCheckpointsAsync(cancellationToken);
            if (updated > 0)
            {
                logger.LogInformation("Forecast checkpoint sweep updated {UpdatedCount} open weather safety review(s).", updated);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Graceful shutdown
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error occurred during weather forecast checkpoint re-evaluation.");
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var interval = TimeSpan.FromMinutes(Math.Max(1, options.Value.ForecastCheckpointIntervalMinutes));

            await RunCheckpointCycleAsync(stoppingToken);

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