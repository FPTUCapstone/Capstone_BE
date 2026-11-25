using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using TripMate.Application.Features.Operator.PostTripComplaints.Deadlines;

namespace TripMate.Infrastructure.Complaints;

internal sealed class OperatorComplaintDeadlineBackgroundService(
    IServiceScopeFactory scopeFactory, OperatorComplaintDeadlineOptions options,
    ILogger<OperatorComplaintDeadlineBackgroundService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using AsyncServiceScope scope = scopeFactory.CreateAsyncScope();
                int expired = await scope.ServiceProvider.GetRequiredService<OperatorComplaintDeadlineProcessor>()
                    .ExpireOverdueAsync(stoppingToken);
                if (expired > 0) logger.LogInformation("Expired {Count} overdue operator complaint response(s).", expired);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception exception) { logger.LogError(exception, "Operator complaint deadline sweep failed."); }
            try { await Task.Delay(options.SweepInterval, stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
        }
    }
}