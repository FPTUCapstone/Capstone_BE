namespace TripMate.Application.Features.TripAlerts.Weather;

public interface ITripWeatherProvider
{
    Task<TripWeatherSnapshot?> GetAsync(
        WeatherForecastRequest request,
        CancellationToken cancellationToken);
}

public sealed record WeatherForecastRequest(
    decimal Latitude,
    decimal Longitude,
    DateTimeOffset PlannedAtUtc);

public sealed record TripWeatherSnapshot(
    DateTimeOffset ForecastAtUtc,
    double RainMmPerHour,
    bool Thunderstorm,
    string? Condition,
    IReadOnlyCollection<TripWeatherOfficialAlert> OfficialAlerts);

public sealed record TripWeatherOfficialAlert(
    string Id,
    string Label,
    string? Description,
    DateTimeOffset StartsAtUtc,
    DateTimeOffset EndsAtUtc);