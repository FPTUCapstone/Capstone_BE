using TripMate.Application.Common.Interfaces;

namespace TripMate.Application.UnitTests.TestUtilities;

internal sealed class NoOpWeatherSafetyReviewNotifier : IWeatherSafetyReviewNotifier
{
    public Task NotifyChangedAsync(
        long operatorUserId,
        WeatherSafetyReviewChanged notification,
        CancellationToken cancellationToken) => Task.CompletedTask;
}
