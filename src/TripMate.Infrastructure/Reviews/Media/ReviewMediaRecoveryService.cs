using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using TripMate.Application.Features.TripReviews.Media;

namespace TripMate.Infrastructure.Reviews.Media;

public sealed class ReviewMediaRecoveryService(IServiceScopeFactory scopes, TimeProvider time,
    ILogger<ReviewMediaRecoveryService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30), time);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                try
                {
                    await using var scope = scopes.CreateAsyncScope();
                    await scope.ServiceProvider.GetRequiredService<ReviewMediaRecoveryRunner>().RunOnceAsync(stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
                catch (Exception)
                {
                    // Raw provider/SQL exceptions can contain credentials or request URLs.
                    logger.LogWarning("Review media recovery run failed; durable operations remain available for reconciliation.");
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }
}