using MediatR;

using TripMate.Application.Common.Models;

namespace TripMate.Application.Features.Weather.PublicForecast;

/// <summary>
/// The read-only forecast of one place in Vietnam for the public weather page. It is not a weather warning: it never
/// touches a tour, booking or Weather Safety Review.
/// </summary>
public sealed record GetPublicWeatherForecastQuery(decimal Latitude, decimal Longitude)
    : IRequest<Result<PublicWeatherForecastResponse>>
{
    // A loose box around Vietnam, its islands included. It only rejects junk coordinates; the FE geocoder already
    // limits the search to Vietnam.
    public const decimal MinLatitude = 6m;
    public const decimal MaxLatitude = 24m;
    public const decimal MinLongitude = 102m;
    public const decimal MaxLongitude = 118m;

    /// <summary>Two decimals is about 1.1 km, so nearby searches share one cache entry and one provider call.</summary>
    public const int CoordinateDecimals = 2;
}

public static class PublicWeatherForecastErrors
{
    public const string Unavailable = "public_weather.unavailable";
}

public interface IPublicWeatherForecastProvider
{
    /// <summary>
    /// The forecast around a location, or <c>null</c> when the integration is off, the provider fails or is out of
    /// quota, or the answer has no usable hourly timeline.
    /// </summary>
    Task<PublicWeatherForecastData?> GetAsync(decimal latitude, decimal longitude, CancellationToken cancellationToken);
}

public sealed record PublicWeatherForecastData(
    DateTimeOffset FetchedAtUtc,
    IReadOnlyList<PublicWeatherHourDto> Hourly,
    IReadOnlyList<PublicWeatherDayDto> Daily);

public sealed record PublicWeatherForecastResponse(
    string Source,
    string Provider,
    PublicWeatherLocationDto Location,
    string TimeZoneId,
    DateTimeOffset FetchedAtUtc,
    PublicWeatherCurrentDto Current,
    IReadOnlyList<PublicWeatherHourDto> Hourly,
    IReadOnlyList<PublicWeatherDayDto> Daily);

public sealed record PublicWeatherLocationDto(decimal Latitude, decimal Longitude, string TimeZoneId);

/// <summary>The forecast for the current hour, not a live observation.</summary>
public sealed record PublicWeatherCurrentDto(
    DateTimeOffset ObservedAtUtc,
    double TemperatureC,
    double ApparentTemperatureC,
    double RainProbabilityPercent,
    double RainMmPerHour,
    double UvIndex,
    double WindSpeedMps,
    bool Thunderstorm,
    int WeatherCode,
    double HumidityPercent,
    double VisibilityKm,
    double CloudCoverPercent);

public sealed record PublicWeatherHourDto(
    DateTimeOffset StartUtc,
    double TemperatureC,
    double ApparentTemperatureC,
    double RainProbabilityPercent,
    double RainMmPerHour,
    double UvIndex,
    double WindSpeedMps,
    bool Thunderstorm,
    int WeatherCode,
    double HumidityPercent,
    double CloudCoverPercent,
    double VisibilityKm);

/// <summary>One local day. <see cref="Date"/> is <c>yyyy-MM-dd</c>; sunrise and sunset are local <c>HH:mm</c>.</summary>
public sealed record PublicWeatherDayDto(
    string Date,
    double MinTempC,
    double MaxTempC,
    double MaxApparentTempC,
    double MaxRainProbabilityPercent,
    double MaxRainMmPerHour,
    double TotalRainMm,
    double MaxUvIndex,
    double MaxWindSpeedMps,
    bool Thunderstorm,
    int WeatherCode,
    double HumidityAvgPercent,
    string SunriseTime,
    string SunsetTime);