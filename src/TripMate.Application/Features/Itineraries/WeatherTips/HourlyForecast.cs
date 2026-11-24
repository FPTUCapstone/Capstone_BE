namespace TripMate.Application.Features.Itineraries.WeatherTips;

/// <summary>One forecast hour. <see cref="StartUtc"/> is the start of that whole UTC hour.</summary>
public sealed record HourlyForecast(
    DateTimeOffset StartUtc,
    double TemperatureC,
    double ApparentTemperatureC,
    double RainProbabilityPercent,
    double RainMmPerHour,
    double UvIndex,
    double WindSpeedMps,
    bool Thunderstorm);