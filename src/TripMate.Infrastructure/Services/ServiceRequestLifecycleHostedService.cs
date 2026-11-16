using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using TripMate.Application.Features.ServiceProviderRequests;

namespace TripMate.Infrastructure.Services;

public sealed class ServiceRequestLifecycleHostedService(IServiceScopeFactory scopes,
    ILogger<ServiceRequestLifecycleHostedService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30));
        do
        {
            try
            {
                using var scope = scopes.CreateScope();
                var processor = scope.ServiceProvider.GetRequiredService<ServiceRequestLifecycleProcessor>();
                // Drain bounded batches before waiting, while releasing every booking transaction.
                while (await processor.ProcessAsync(stoppingToken) == 100)
                    stoppingToken.ThrowIfCancellationRequested();
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Service booking request lifecycle processing failed. Retrying on the next interval.");
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}