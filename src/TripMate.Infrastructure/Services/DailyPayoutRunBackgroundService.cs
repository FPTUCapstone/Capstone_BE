using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using TripMate.Application.Features.Payouts.DailySettlement;

namespace TripMate.Infrastructure.Services;

/// <summary>Polls <see cref="DailyPayoutRunProcessor.RunIfDueAsync"/> (SRS v11 BR-141, UC-71).
/// Whichever poll first notices "Vietnam time is past 10:00 and no run exists yet today" performs
/// the run — including the first poll after a restart, which is PD-16's catch-up behaviour.</summary>
internal sealed class DailyPayoutRunBackgroundService(
    IServiceScopeFactory scopeFactory,
    DailyPayoutOptions options,
    ILogger<DailyPayoutRunBackgroundService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using AsyncServiceScope scope = scopeFactory.CreateAsyncScope();
                var processor = scope.ServiceProvider.GetRequiredService<DailyPayoutRunProcessor>();
                var outcome = await processor.RunIfDueAsync(stoppingToken);
                if (outcome == DailyPayoutRunOutcome.Ran)
                    logger.LogInformation("Daily payout run completed.");
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception exception)
            {
                logger.LogError(exception, "Daily payout run failed.");
            }

            try { await Task.Delay(options.RunPollInterval, stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
        }
    }
}