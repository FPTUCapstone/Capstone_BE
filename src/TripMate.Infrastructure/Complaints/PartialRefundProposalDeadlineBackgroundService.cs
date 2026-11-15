using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using TripMate.Application.Features.Complaints.PartialRefundProposals;

namespace TripMate.Infrastructure.Complaints;

/// <summary>Runs the 3-day branch of the hourly Complaint Deadline Job (SRS §3.1.4 #16).</summary>
internal sealed class PartialRefundProposalDeadlineBackgroundService(
    IServiceScopeFactory scopeFactory,
    PartialRefundProposalDeadlineOptions options,
    ILogger<PartialRefundProposalDeadlineBackgroundService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using AsyncServiceScope scope = scopeFactory.CreateAsyncScope();
                var processor = scope.ServiceProvider.GetRequiredService<PartialRefundProposalDeadlineProcessor>();
                int closed = await processor.CloseOverdueAsync(stoppingToken);
                if (closed > 0)
                    logger.LogInformation("Declined {Count} unanswered partial refund proposal(s).", closed);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception exception)
            {
                logger.LogError(exception, "Partial refund proposal deadline sweep failed.");
            }

            try { await Task.Delay(options.SweepInterval, stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
        }
    }
}