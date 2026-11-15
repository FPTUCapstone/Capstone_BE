using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using TripMate.Application.Features.Bookings.Refunds;

namespace TripMate.Infrastructure.Services;

internal sealed class RefundProcessingBackgroundService(
    IServiceScopeFactory scopeFactory,
    RefundProcessingOptions options,
    ILogger<RefundProcessingBackgroundService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using AsyncServiceScope scope = scopeFactory.CreateAsyncScope();
                var processor = scope.ServiceProvider.GetRequiredService<RefundProcessor>();
                RefundSweepResult result = await processor.ProcessDueRefundsAsync(stoppingToken);
                if (result.ProcessedCount > 0 || result.DeferredCount > 0)
                    logger.LogInformation("Refund sweep processed {Processed} and deferred {Deferred} item(s).",
                        result.ProcessedCount, result.DeferredCount);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception exception)
            {
                logger.LogError(exception, "Automatic refund sweep failed.");
            }

            try { await Task.Delay(options.SweepInterval, stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
        }
    }
}