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
    DateTimeOffset PlannedAtUtc,
    bool UseRealtime = false);

public enum TripWeatherBasis
{
    ForecastAtPlannedTime,
    CurrentAtDestination,
    OfficialEvent,
}

public sealed record TripWeatherSnapshot(
    DateTimeOffset ForecastAtUtc,
    double RainMmPerHour,
    bool Thunderstorm,
    string? Condition,
    IReadOnlyCollection<TripWeatherOfficialAlert> OfficialAlerts,
    TripWeatherBasis Basis = TripWeatherBasis.ForecastAtPlannedTime);

public sealed record TripWeatherOfficialAlert(
    string Id,
    string Label,
    string? Description,
    DateTimeOffset StartsAtUtc,
    DateTimeOffset EndsAtUtc);