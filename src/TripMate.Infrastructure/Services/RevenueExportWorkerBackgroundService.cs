using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using TripMate.Application.Features.Operator.Revenue.Exports.Common;
using TripMate.Application.Features.Operator.Revenue.Exports.Worker;

namespace TripMate.Infrastructure.Services;

/// <summary>UC-45: periodically claims and renders Queued revenue exports left by the synchronous threshold.</summary>
internal sealed class RevenueExportWorkerBackgroundService(
    IServiceScopeFactory scopeFactory,
    RevenueExportOptions options,
    ILogger<RevenueExportWorkerBackgroundService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using AsyncServiceScope scope = scopeFactory.CreateAsyncScope();
                RevenueExportWorkerProcessor processor =
                    scope.ServiceProvider.GetRequiredService<RevenueExportWorkerProcessor>();
                int processedCount = await processor.ProcessQueuedBatchAsync(stoppingToken);
                if (processedCount > 0)
                {
                    logger.LogInformation("Processed {ProcessedCount} queued revenue export(s).", processedCount);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogError("Revenue export worker sweep failed with {ExceptionType}.", exception.GetType().Name);
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(options.WorkerPollIntervalSeconds), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }
}