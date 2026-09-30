using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace TripMate.Infrastructure.Services;

internal sealed class TourMediaCleanupBackgroundService(
    IServiceScopeFactory scopeFactory,
    IOptions<TourMediaCleanupOptions> options,
    ILogger<TourMediaCleanupBackgroundService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        TourMediaCleanupOptions settings = options.Value;
        logger.LogInformation(
            "Tour media cleanup worker started. Poll interval: {PollInterval}; lease duration: {LeaseDuration}; batch size: {BatchSize}.",
            settings.PollInterval,
            settings.LeaseDuration,
            settings.BatchSize);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using AsyncServiceScope scope = scopeFactory.CreateAsyncScope();
                TourMediaCleanupProcessor processor = scope.ServiceProvider
                    .GetRequiredService<TourMediaCleanupProcessor>();
                int processed = await processor.ProcessDueBatchAsync(
                    settings.LeaseDuration,
                    settings.BatchSize,
                    stoppingToken);
                if (processed > 0)
                {
                    logger.LogInformation(
                        "Tour media cleanup worker processed {ProcessedCount} outbox items.",
                        processed);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                // Keep raw provider/SQL exception details out of logs; the lease will expire and be reclaimed.
                logger.LogError(
                    "Tour media cleanup batch failed with {ExceptionType}; leased items will be recovered after lease expiry.",
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

        logger.LogInformation("Tour media cleanup worker stopped.");
    }
}