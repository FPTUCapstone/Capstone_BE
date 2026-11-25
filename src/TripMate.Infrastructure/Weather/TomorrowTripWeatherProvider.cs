using System.Globalization;
using System.Text.Json;

using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;

using TripMate.Application.Features.TripAlerts.Weather;

namespace TripMate.Infrastructure.Weather;

internal sealed class TomorrowTripWeatherProvider(
    HttpClient httpClient,
    IMemoryCache cache,
    IOptions<TomorrowWeatherOptions> options) : ITripWeatherProvider
{
    private readonly TomorrowWeatherOptions options = options.Value;

    public async Task<TripWeatherSnapshot?> GetAsync(
        WeatherForecastRequest request,
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

        var bucket = request.PlannedAtUtc.ToUnixTimeSeconds() / 600;
        var cacheKey = FormattableString.Invariant(
            $"tomorrow:{request.Latitude}:{request.Longitude}:{request.UseRealtime}:{bucket}");
        if (cache.TryGetValue(cacheKey, out TripWeatherSnapshot? cached)
            && cached is not null
            && (!request.UseRealtime || (cached.ForecastAtUtc <= request.PlannedAtUtc
                && request.PlannedAtUtc - cached.ForecastAtUtc <= TimeSpan.FromMinutes(10))))
        {
            return cached;
        }

        var location = string.Concat(
            request.Latitude.ToString(CultureInfo.InvariantCulture), ",",
            request.Longitude.ToString(CultureInfo.InvariantCulture));
        var endpoint = request.UseRealtime ? "v4/weather/realtime" : "v4/weather/forecast";
        var uri = $"{endpoint}?location={Uri.EscapeDataString(location)}&units=metric"
            + (request.UseRealtime ? string.Empty : "&timesteps=1h");
        using var message = new HttpRequestMessage(HttpMethod.Get, uri);
        message.Headers.Add("apikey", options.ApiKey);
        using var response = await httpClient.SendAsync(message, cancellationToken);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);

        JsonElement record;
        if (request.UseRealtime)
        {
            if (!document.RootElement.TryGetProperty("data", out record)) return null;
        }
        else
        {
            if (!document.RootElement.TryGetProperty("timelines", out var timelines)
                || !timelines.TryGetProperty("hourly", out var hourly)
                || hourly.ValueKind != JsonValueKind.Array) return null;
            record = hourly.EnumerateArray().FirstOrDefault(item =>
                TryReadTime(item, out var time)
                && time <= request.PlannedAtUtc
                && request.PlannedAtUtc < time.AddHours(1));
            if (record.ValueKind == JsonValueKind.Undefined) return null;
        }

        if (!TryReadTime(record, out var weatherAt)
            || !record.TryGetProperty("values", out var values)
            || !values.TryGetProperty("rainIntensity", out var rain)
            || !rain.TryGetDouble(out var intensity)
            || !double.IsFinite(intensity)
            || intensity < 0
            || !values.TryGetProperty("weatherCode", out var code)
            || !code.TryGetInt32(out var weatherCode))
        {
            return null;
        }

        if (request.UseRealtime && (weatherAt > request.PlannedAtUtc
            || request.PlannedAtUtc - weatherAt > TimeSpan.FromMinutes(10)))
        {
            return null;
        }

        var snapshot = new TripWeatherSnapshot(weatherAt, intensity,
            weatherCode == 8000, null, [], request.UseRealtime
                ? TripWeatherBasis.CurrentAtDestination
                : TripWeatherBasis.ForecastAtPlannedTime);
        cache.Set(cacheKey, snapshot, options.CacheDuration);
        return snapshot;
    }

    private static bool TryReadTime(JsonElement record, out DateTimeOffset time)
    {
        time = default;
        return record.TryGetProperty("time", out var value)
            && value.ValueKind == JsonValueKind.String
            && DateTimeOffset.TryParse(value.GetString(), CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal, out time);
    }
}