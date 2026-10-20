using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using TripMate.Application.Features.Operator.Revenue.Exports.Common;
using TripMate.Application.Features.Operator.Revenue.Exports.Worker;

namespace TripMate.Infrastructure.Services;

/// <summary>UC-45: periodically deletes the private asset and marks Ready exports Expired once their retention window passes.</summary>
internal sealed class RevenueExportExpiryBackgroundService(
    IServiceScopeFactory scopeFactory,
    RevenueExportOptions options,
    ILogger<RevenueExportExpiryBackgroundService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using AsyncServiceScope scope = scopeFactory.CreateAsyncScope();
                RevenueExportExpiryProcessor processor =
                    scope.ServiceProvider.GetRequiredService<RevenueExportExpiryProcessor>();
                int expiredCount = await processor.ExpireDueExportsAsync(stoppingToken);
                if (expiredCount > 0)
                {
                    logger.LogInformation("Expired {ExpiredCount} revenue export(s).", expiredCount);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogError("Revenue export expiry sweep failed with {ExceptionType}.", exception.GetType().Name);
            }

            try
            {
                await Task.Delay(TimeSpan.FromMinutes(options.ExpiryCleanupIntervalMinutes), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }
}
