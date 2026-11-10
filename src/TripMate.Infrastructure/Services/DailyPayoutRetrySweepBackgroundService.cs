using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using TripMate.Application.Features.Payouts.DailySettlement;

namespace TripMate.Infrastructure.Services;

/// <summary>Polls <see cref="DailyPayoutRetrySweepProcessor.SweepAsync"/> (PD-7, PD-7a). The
/// sweep itself decides whether any batch is actually due for a retry (hourly), so polling more
/// often than that is harmless.</summary>
internal sealed class DailyPayoutRetrySweepBackgroundService(
    IServiceScopeFactory scopeFactory,
    DailyPayoutOptions options,
    ILogger<DailyPayoutRetrySweepBackgroundService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using AsyncServiceScope scope = scopeFactory.CreateAsyncScope();
                var sweeper = scope.ServiceProvider.GetRequiredService<DailyPayoutRetrySweepProcessor>();
                var acted = await sweeper.SweepAsync(stoppingToken);
                if (acted > 0)
                    logger.LogInformation("Daily payout retry sweep acted on {Count} batch(es).", acted);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception exception)
            {
                logger.LogError(exception, "Daily payout retry sweep failed.");
            }

            try { await Task.Delay(options.RetrySweepPollInterval, stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
        }
    }
}