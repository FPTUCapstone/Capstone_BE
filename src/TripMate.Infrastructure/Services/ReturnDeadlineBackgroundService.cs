using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using TripMate.Application.Features.Bookings.Refunds;

namespace TripMate.Infrastructure.Services;

internal sealed class ReturnDeadlineBackgroundService(
    IServiceScopeFactory scopeFactory,
    ReturnDeadlineOptions options,
    ILogger<ReturnDeadlineBackgroundService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using AsyncServiceScope scope = scopeFactory.CreateAsyncScope();
                var processor = scope.ServiceProvider.GetRequiredService<ReturnDeadlineProcessor>();
                int recordedCount = await processor.RecordOverdueDebtsAsync(stoppingToken);
                if (recordedCount > 0)
                    logger.LogInformation("Recorded {Count} overdue Tour Operator debt(s) (BR-144).", recordedCount);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception exception)
            {
                logger.LogError(exception, "BR-144 return-deadline scan failed.");
            }

            try { await Task.Delay(options.ScanInterval, stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
        }
    }
}