namespace TripMate.Application.Common.Interfaces;

public interface IWeatherSafetyReviewNotifier
{
    Task NotifyChangedAsync(
        long operatorUserId,
        WeatherSafetyReviewChanged notification,
        CancellationToken cancellationToken);
}

public sealed record WeatherSafetyReviewChanged(
    long ReviewId,
    long TourScheduleId,
    string ChangeType,
    DateTimeOffset OccurredAtUtc);

public static class WeatherSafetyReviewChangeTypes
{
    public const string Created = "Created";
    public const string Updated = "Updated";
    public const string Escalated = "Escalated";
    public const string Closed = "Closed";
}

internal sealed class NullWeatherSafetyReviewNotifier : IWeatherSafetyReviewNotifier
{
    public Task NotifyChangedAsync(
        long operatorUserId,
        WeatherSafetyReviewChanged notification,
        CancellationToken cancellationToken) => Task.CompletedTask;
}
