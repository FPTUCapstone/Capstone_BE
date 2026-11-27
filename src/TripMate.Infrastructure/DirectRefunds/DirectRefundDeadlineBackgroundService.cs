using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using TripMate.Application.Features.Bookings.DirectRefunds.Deadline;

namespace TripMate.Infrastructure.DirectRefunds;

/// <summary>Hourly sweep: auto-completes timed-out traveler confirmations and flags overdue transfers.</summary>
internal sealed class DirectRefundDeadlineBackgroundService(
    IServiceScopeFactory scopeFactory,
    DirectRefundDeadlineOptions options,
    ILogger<DirectRefundDeadlineBackgroundService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using AsyncServiceScope scope = scopeFactory.CreateAsyncScope();
                var processor = scope.ServiceProvider.GetRequiredService<DirectRefundDeadlineProcessor>();
                int completed = await processor.ProcessOverdueAsync(stoppingToken);
                if (completed > 0)
                    logger.LogInformation("Auto-completed {Count} direct refund case(s) after 72-hour window.", completed);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception exception)
            {
                logger.LogError(exception, "Direct refund deadline sweep failed.");
            }

            try { await Task.Delay(options.SweepInterval, stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
        }
    }
}
