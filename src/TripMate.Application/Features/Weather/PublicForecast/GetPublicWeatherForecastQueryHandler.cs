using MediatR;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Common.Models;

namespace TripMate.Application.Features.Weather.PublicForecast;

public sealed class GetPublicWeatherForecastQueryHandler(
    IPublicWeatherForecastProvider provider,
    IDateTimeProvider clock)
    : IRequestHandler<GetPublicWeatherForecastQuery, Result<PublicWeatherForecastResponse>>
{
    public const string TimeZoneId = "Asia/Ho_Chi_Minh";
    public const string Source = "api";
    public const string ProviderName = "Tomorrow.io";
    public const int HourlyPoints = 24;

    public async Task<Result<PublicWeatherForecastResponse>> Handle(
        GetPublicWeatherForecastQuery request,
        CancellationToken cancellationToken)
    {
        var latitude = Round(request.Latitude);
        var longitude = Round(request.Longitude);

        var data = await provider.GetAsync(latitude, longitude, cancellationToken);
        var now = clock.UtcNow;
        var thisHour = new DateTimeOffset(now.Year, now.Month, now.Day, now.Hour, 0, 0, TimeSpan.Zero);
        var hourly = data?.Hourly
            .Where(hour => hour.StartUtc >= thisHour)
            .OrderBy(hour => hour.StartUtc)
            .Take(HourlyPoints)
            .ToArray();
        if (data is null || hourly is null || hourly.Length == 0)
        {
            return Result.Failure<PublicWeatherForecastResponse>(
                PublicWeatherForecastErrors.Unavailable,
                "Dịch vụ thời tiết đang bận, vui lòng thử lại sau ít phút.");
        }

        var current = hourly[0];
        return Result.Success(new PublicWeatherForecastResponse(
            Source,
            ProviderName,
            new PublicWeatherLocationDto(latitude, longitude, TimeZoneId),
            TimeZoneId,
            data.FetchedAtUtc,
            new PublicWeatherCurrentDto(
                current.StartUtc, current.TemperatureC, current.ApparentTemperatureC, current.RainProbabilityPercent,
                current.RainMmPerHour, current.UvIndex, current.WindSpeedMps, current.Thunderstorm, current.WeatherCode,
                current.HumidityPercent, current.VisibilityKm, current.CloudCoverPercent),
            hourly,
            data.Daily));
    }

    private static decimal Round(decimal value) =>
        Math.Round(value, GetPublicWeatherForecastQuery.CoordinateDecimals, MidpointRounding.AwayFromZero);
}