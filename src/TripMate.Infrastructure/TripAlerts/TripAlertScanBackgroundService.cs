using FirebaseAdmin;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Features.TripAlerts.Common;
using TripMate.Application.Features.TripAlerts.Scanning;
using TripMate.Infrastructure.Weather;

namespace TripMate.Infrastructure.TripAlerts;

internal sealed class TripAlertScanBackgroundService(
    IServiceScopeFactory scopeFactory,
    IOptions<TripAlertOptions> options,
    IOptions<TomorrowWeatherOptions> weatherOptions,
    ILogger<TripAlertScanBackgroundService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        LogStartupDiagnostics();
        using var timer = new PeriodicTimer(options.Value.ScanInterval);
        do
        {
            try
            {
                await using AsyncServiceScope scope = scopeFactory.CreateAsyncScope();
                await scope.ServiceProvider.GetRequiredService<TripAlertScanner>()
                    .ScanAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogError("Trip alert scan failed with {ExceptionType}.", exception.GetType().Name);
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    private void LogStartupDiagnostics()
    {
        bool pushReady;
        try
        {
            using var scope = scopeFactory.CreateScope();
            _ = scope.ServiceProvider.GetRequiredService<IFirebaseAuthService>(); // Initializes the shared FirebaseApp.
            pushReady = FirebaseApp.DefaultInstance is not null;
        }
        catch (Exception)
        {
            pushReady = false;
        }

        foreach (var warning in TripAlertStartupDiagnostics.Warnings(weatherOptions.Value.Enabled, pushReady))
        {
            logger.LogWarning("{Message}", warning);
        }
    }
}