using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using TripMate.Application.Features.Admin.PayoutSettlements.Processing;

namespace TripMate.Infrastructure.Services;

/// <summary>
/// Runs the UC-65 payout-settlement sweep on a fixed interval. A failing sweep is logged and retried
/// on the next tick; it never takes the host down.
/// </summary>
internal sealed class PayoutSettlementBackgroundService(
    IServiceScopeFactory scopeFactory,
    PayoutSettlementOptions options,
    ILogger<PayoutSettlementBackgroundService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            await RunSweepAsync(stoppingToken);

            try
            {
                await Task.Delay(options.SweepInterval, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }

    internal async Task RunSweepAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var sweeper = scope.ServiceProvider.GetRequiredService<IPayoutSettlementSweeper>();
            var result = await sweeper.ProcessDueAsync(cancellationToken);
            if (result.FailedWorkItemIds.Count > 0)
            {
                logger.LogError(
                    "Payout settlement sweep handled {Handled} item(s); {Failed} failed unexpectedly: {WorkItemIds}.",
                    result.ItemsHandled, result.FailedWorkItemIds.Count, string.Join(",", result.FailedWorkItemIds));
            }
            else if (result.ItemsHandled > 0)
            {
                logger.LogInformation("Payout settlement sweep handled {Handled} item(s).", result.ItemsHandled);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Shutting down.
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Payout settlement sweep failed.");
        }
    }
}
