using System.Globalization;
using System.Text.Json;

using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Features.Weather.PublicForecast;

namespace TripMate.Infrastructure.Weather;

/// <summary>
/// Reads the hourly and daily forecast of one location from Tomorrow.io in a single call. A point that lacks any field
/// is dropped, never filled in. Every failure, quota exhaustion included, is logged and returned as <c>null</c>, and
/// failures are not cached so the next request tries again.
/// </summary>
internal sealed class TomorrowPublicForecastProvider(
    HttpClient httpClient,
    IMemoryCache cache,
    IOptions<PublicWeatherOptions> options,
    IDateTimeProvider clock,
    ILogger<TomorrowPublicForecastProvider> logger) : IPublicWeatherForecastProvider
{
    private const int ThunderstormWeatherCode = 8000;

    // Vietnam has no daylight saving, so a fixed offset is the same as the zone and needs no tz database.
    private static readonly TimeSpan LocalOffset = TimeSpan.FromHours(7);

    private readonly PublicWeatherOptions options = options.Value;

    public async Task<PublicWeatherForecastData?> GetAsync(
        decimal latitude,
        decimal longitude,
        CancellationToken cancellationToken)
    {
        if (!options.Enabled || string.IsNullOrWhiteSpace(options.ApiKey))
        {
            return null;
        }

        var location = string.Concat(
            latitude.ToString(CultureInfo.InvariantCulture), ",",
            longitude.ToString(CultureInfo.InvariantCulture));
        var cacheKey = $"public-weather:{location}";
        if (cache.TryGetValue(cacheKey, out PublicWeatherForecastData? cached) && cached is not null)
        {
            return cached;
        }

        var uri = $"v4/weather/forecast?location={Uri.EscapeDataString(location)}&units=metric"
            + $"&timesteps={Uri.EscapeDataString("1h,1d")}";
        try
        {
            using var message = new HttpRequestMessage(HttpMethod.Get, uri);
            message.Headers.Add("apikey", options.ApiKey);
            using var response = await httpClient.SendAsync(message, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("Public weather forecast returned {StatusCode}.", (int)response.StatusCode);
                return null;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            if (!document.RootElement.TryGetProperty("timelines", out var timelines)
                || !timelines.TryGetProperty("hourly", out var hourly)
                || hourly.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            var data = new PublicWeatherForecastData(
                clock.UtcNow,
                ReadAll<PublicWeatherHourDto>(hourly, TryReadHour).OrderBy(hour => hour.StartUtc).ToArray(),
                timelines.TryGetProperty("daily", out var daily) && daily.ValueKind == JsonValueKind.Array
                    ? ReadAll<PublicWeatherDayDto>(daily, TryReadDay).OrderBy(day => day.Date, StringComparer.Ordinal).ToArray()
                    : []);
            cache.Set(cacheKey, data, options.CacheDuration);
            return data;
        }
        catch (Exception exception) when (exception is HttpRequestException or JsonException
            or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(exception, "Public weather forecast is unavailable.");
            return null;
        }
    }

    private delegate bool PointReader<T>(JsonElement record, out T point);

    private static List<T> ReadAll<T>(JsonElement array, PointReader<T> read)
    {
        var points = new List<T>();
        foreach (var record in array.EnumerateArray())
        {
            if (read(record, out var point))
            {
                points.Add(point);
            }
        }

        return points;
    }

    private static bool TryReadHour(JsonElement record, out PublicWeatherHourDto hour)
    {
        hour = null!;
        if (!TryTime(record, "time", out var startUtc)
            || !record.TryGetProperty("values", out var values)
            || !TryNumber(values, "temperature", out var temperature)
            || !TryNumber(values, "temperatureApparent", out var apparent)
            || !TryNumber(values, "precipitationProbability", out var probability)
            || !TryNumber(values, "rainIntensity", out var rain)
            || !TryNumber(values, "uvIndex", out var uv)
            || !TryNumber(values, "windSpeed", out var wind)
            || !TryCode(values, "weatherCode", out var code)
            || !TryNumber(values, "humidity", out var humidity)
            || !TryNumber(values, "cloudCover", out var cloudCover)
            || !TryNumber(values, "visibility", out var visibility)
            || rain < 0)
        {
            return false;
        }

        hour = new PublicWeatherHourDto(startUtc, temperature, apparent, probability, rain, uv, wind,
            code == ThunderstormWeatherCode, code, humidity, cloudCover, visibility);
        return true;
    }

    private static bool TryReadDay(JsonElement record, out PublicWeatherDayDto day)
    {
        day = null!;
        if (!TryTime(record, "time", out var startUtc)
            || !record.TryGetProperty("values", out var values)
            || !TryNumber(values, "temperatureMin", out var min)
            || !TryNumber(values, "temperatureMax", out var max)
            || !TryNumber(values, "temperatureApparentMax", out var apparentMax)
            || !TryNumber(values, "precipitationProbabilityMax", out var probability)
            || !TryNumber(values, "rainIntensityMax", out var rainMax)
            || !TryNumber(values, "rainAccumulationSum", out var rainTotal)
            || !TryNumber(values, "uvIndexMax", out var uv)
            || !TryNumber(values, "windSpeedMax", out var wind)
            || !TryCode(values, "weatherCodeMax", out var code)
            || !TryNumber(values, "humidityAvg", out var humidity)
            || !TryTime(values, "sunriseTime", out var sunrise)
            || !TryTime(values, "sunsetTime", out var sunset))
        {
            return false;
        }

        day = new PublicWeatherDayDto(
            startUtc.ToOffset(LocalOffset).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            min, max, apparentMax, probability, rainMax, rainTotal, uv, wind,
            code == ThunderstormWeatherCode, code, humidity, LocalClock(sunrise), LocalClock(sunset));
        return true;
    }

    private static string LocalClock(DateTimeOffset utc) =>
        utc.ToOffset(LocalOffset).ToString("HH:mm", CultureInfo.InvariantCulture);

    private static bool TryTime(JsonElement element, string name, out DateTimeOffset value)
    {
        value = default;
        if (!element.TryGetProperty(name, out var property)
            || property.ValueKind != JsonValueKind.String
            || !DateTimeOffset.TryParse(property.GetString(), CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal, out var parsed))
        {
            return false;
        }

        value = parsed.ToUniversalTime();
        return true;
    }

    private static bool TryCode(JsonElement values, string name, out int code)
    {
        code = 0;
        return values.TryGetProperty(name, out var element) && element.TryGetInt32(out code);
    }

    private static bool TryNumber(JsonElement values, string name, out double value)
    {
        value = 0;
        return values.TryGetProperty(name, out var element)
            && element.ValueKind == JsonValueKind.Number
            && element.TryGetDouble(out value)
            && double.IsFinite(value);
    }
}