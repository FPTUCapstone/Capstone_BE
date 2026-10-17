using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using TripMate.Application.Features.Traveler.Bookings.Common;
using TripMate.Application.Features.Traveler.Bookings.Expiry;

namespace TripMate.Infrastructure.Services;

/// <summary>Periodically expires stale payment holds so capacity is returned without a new request.</summary>
internal sealed class PendingBookingExpiryBackgroundService(
    IServiceScopeFactory scopeFactory,
    TourBookingOptions options,
    ILogger<PendingBookingExpiryBackgroundService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using AsyncServiceScope scope = scopeFactory.CreateAsyncScope();
                PendingBookingExpiryProcessor processor = scope.ServiceProvider
                    .GetRequiredService<PendingBookingExpiryProcessor>();
                PendingBookingExpirySweepResult result = await processor.ExpireDueBookingsAsync(stoppingToken);
                if (result.ExpiredCount > 0)
                    logger.LogInformation("Expired {ExpiredBookingCount} pending tour booking(s).", result.ExpiredCount);
                foreach (PendingBookingExpiryFailure failure in result.Failures)
                {
                    logger.LogError(
                        "Could not expire pending tour booking {BookingId}; {ExceptionType} will be retried on the next sweep.",
                        failure.BookingId,
                        failure.ExceptionType);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogError("Pending booking expiry sweep failed with {ExceptionType}.", exception.GetType().Name);
            }

            try
            {
                await Task.Delay(options.ExpirySweepInterval, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }
}
