using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using TripMate.Application.Features.Bookings.Refunds;

namespace TripMate.Infrastructure.Services;

internal sealed class OperatorConfirmationDeadlineBackgroundService(
    IServiceScopeFactory scopeFactory,
    OperatorConfirmationDeadlineOptions options,
    ILogger<OperatorConfirmationDeadlineBackgroundService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using AsyncServiceScope scope = scopeFactory.CreateAsyncScope();
                var processor = scope.ServiceProvider.GetRequiredService<OperatorConfirmationDeadlineProcessor>();
                int confirmedCount = await processor.AutoConfirmOverdueRefundsAsync(stoppingToken);
                if (confirmedCount > 0)
                    logger.LogInformation("Auto-confirmed {Count} overdue refund(s) (BR-187).", confirmedCount);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception exception)
            {
                logger.LogError(exception, "BR-187 auto-confirmation scan failed.");
            }

            try { await Task.Delay(options.ScanInterval, stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
        }
    }
}