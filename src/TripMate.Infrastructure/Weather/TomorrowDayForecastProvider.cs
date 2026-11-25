using System.Globalization;
using System.Text.Json;

using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;

using TripMate.Application.Features.Itineraries.WeatherTips;

namespace TripMate.Infrastructure.Weather;

/// <summary>
/// Reads the hourly forecast of one location from Tomorrow.io for the itinerary weather tips. An hour that lacks any
/// field the tips need is dropped, never filled in, so a missing value can never turn into a made-up "no rain".
/// </summary>
internal sealed class TomorrowDayForecastProvider(
    HttpClient httpClient,
    IMemoryCache cache,
    IOptions<TomorrowWeatherOptions> options) : IDayForecastProvider
{
    internal static readonly TimeSpan DayCacheDuration = TimeSpan.FromMinutes(30);

    private const int ThunderstormWeatherCode = 8000;

    private readonly TomorrowWeatherOptions options = options.Value;

    public async Task<IReadOnlyList<HourlyForecast>?> GetHourlyAsync(
        decimal latitude,
        decimal longitude,
        CancellationToken cancellationToken)
    {
        if (!options.Enabled)
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(options.ApiKey))
        {
            throw new InvalidOperationException("Tomorrow weather is enabled but its API key is missing.");
        }

        var location = string.Concat(
            latitude.ToString(CultureInfo.InvariantCulture), ",",
            longitude.ToString(CultureInfo.InvariantCulture));
        var cacheKey = $"tomorrow-day:{location}";
        if (cache.TryGetValue(cacheKey, out IReadOnlyList<HourlyForecast>? cached) && cached is not null)
        {
            return cached;
        }

        var uri = $"v4/weather/forecast?location={Uri.EscapeDataString(location)}&units=metric&timesteps=1h";
        using var message = new HttpRequestMessage(HttpMethod.Get, uri);
        message.Headers.Add("apikey", options.ApiKey);
        using var response = await httpClient.SendAsync(message, cancellationToken);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);

        if (!document.RootElement.TryGetProperty("timelines", out var timelines)
            || !timelines.TryGetProperty("hourly", out var hourly)
            || hourly.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var hours = new List<HourlyForecast>();
        foreach (var record in hourly.EnumerateArray())
        {
            if (TryRead(record, out var hour))
            {
                hours.Add(hour);
            }
        }

        IReadOnlyList<HourlyForecast> ordered = hours.OrderBy(hour => hour.StartUtc).ToArray();
        cache.Set(cacheKey, ordered, DayCacheDuration);
        return ordered;
    }

    private static bool TryRead(JsonElement record, out HourlyForecast hour)
    {
        hour = null!;
        if (!record.TryGetProperty("time", out var time)
            || time.ValueKind != JsonValueKind.String
            || !DateTimeOffset.TryParse(time.GetString(), CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal, out var startUtc)
            || !record.TryGetProperty("values", out var values)
            || !TryNumber(values, "temperature", out var temperature)
            || !TryNumber(values, "temperatureApparent", out var apparent)
            || !TryNumber(values, "precipitationProbability", out var probability)
            || !TryNumber(values, "rainIntensity", out var rain)
            || !TryNumber(values, "uvIndex", out var uv)
            || !TryNumber(values, "windSpeed", out var wind)
            || !values.TryGetProperty("weatherCode", out var code)
            || !code.TryGetInt32(out var weatherCode)
            || rain < 0)
        {
            return false;
        }

        hour = new HourlyForecast(
            startUtc.ToUniversalTime(), temperature, apparent, probability, rain, uv, wind,
            weatherCode == ThunderstormWeatherCode);
        return true;
    }

    private static bool TryNumber(JsonElement values, string name, out double value)
    {
        value = 0;
        return values.TryGetProperty(name, out var element)
            && element.TryGetDouble(out value)
            && double.IsFinite(value);
    }
}