using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TripMate.Application.Features.Providers.CommissionStatements;

namespace TripMate.Infrastructure.Services;

public sealed class CommissionStatementLifecycleHostedService(
    IServiceScopeFactory scopes,
    ILogger<CommissionStatementLifecycleHostedService> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(10);
    private string? _lastPeriodGenerated;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval);
        do
        {
            try
            {
                using var scope = scopes.CreateScope();
                var service = scope.ServiceProvider.GetRequiredService<ICommissionStatementService>();

                var nowUtc = DateTimeOffset.UtcNow;
                var nowVn = nowUtc.ToOffset(TimeSpan.FromHours(7));

                // On the 1st day of the month, generate statements for previous month (BR-199, §3.10.7)
                var currentPeriod = $"{nowVn.Year:D4}-{nowVn.Month:D2}";
                if (nowVn.Day == 1 && _lastPeriodGenerated != currentPeriod)
                {
                    logger.LogInformation("Triggering monthly commission statement generation on 1st of month.");
                    var summary = await service.GenerateStatementsAsync(null, stoppingToken);
                    _lastPeriodGenerated = currentPeriod;
                    logger.LogInformation("Generated {Count} commission statements for period {Period}.", summary.GeneratedCount, summary.Period);
                }

                // Daily overdue checks and 15-day account locks (BR-200, §3.10.7)
                var overdueSummary = await service.ProcessOverdueStatementsAsync(nowUtc, stoppingToken);
                if (overdueSummary.MarkedOverdueCount > 0 || overdueSummary.AccountsLockedCount > 0)
                {
                    logger.LogInformation("Commission overdue processing: {Overdue} marked overdue, {Locked} accounts locked.",
                        overdueSummary.MarkedOverdueCount, overdueSummary.AccountsLockedCount);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Commission statement lifecycle processing failed. Retrying on next interval.");
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
