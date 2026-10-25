using System.Globalization;
using System.Text.Json;

using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;

using TripMate.Application.Features.TripAlerts.Weather;

namespace TripMate.Infrastructure.Weather;

internal sealed class OpenWeather4TripWeatherProvider(
    HttpClient httpClient,
    IMemoryCache cache,
    IOptions<OpenWeatherOptions> options) : ITripWeatherProvider
{
    private readonly OpenWeatherOptions options = options.Value;

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
            throw new InvalidOperationException("OpenWeather is enabled but its API key is missing.");
        }

        var bucket = request.PlannedAtUtc.ToUnixTimeSeconds() / 600;
        var cacheKey = FormattableString.Invariant(
            $"openweather4:{request.Latitude:0.0000}:{request.Longitude:0.0000}:{bucket}");
        if (cache.TryGetValue(cacheKey, out TripWeatherSnapshot? cached))
        {
            return cached;
        }

        JsonElement? nearest = await LoadNearestAsync("15min", request, cancellationToken)
            ?? await LoadNearestAsync("1h", request, cancellationToken);
        if (!nearest.HasValue)
        {
            return null;
        }

        var record = nearest.Value;
        var officialAlerts = options.EnableOfficialAlerts
            ? await LoadAlertsAsync(record, cancellationToken)
            : [];
        var snapshot = new TripWeatherSnapshot(
            DateTimeOffset.FromUnixTimeSeconds(record.GetProperty("dt").GetInt64()),
            ReadRain(record),
            ReadThunderstorm(record),
            ReadCondition(record),
            officialAlerts);
        cache.Set(cacheKey, snapshot, options.CacheDuration);
        return snapshot;
    }

    private async Task<JsonElement?> LoadNearestAsync(
        string step,
        WeatherForecastRequest request,
        CancellationToken cancellationToken)
    {
        using JsonDocument timeline = await GetJsonAsync(
            $"data/4.0/onecall/timeline/{step}?lat={Format(request.Latitude)}&lon={Format(request.Longitude)}&appid={Uri.EscapeDataString(options.ApiKey!)}",
            cancellationToken);
        if (!timeline.RootElement.TryGetProperty("data", out JsonElement data)
            || data.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        JsonElement? nearest = data.EnumerateArray()
            .Where(item => item.TryGetProperty("dt", out _))
            .OrderBy(item => Math.Abs(item.GetProperty("dt").GetInt64()
                - request.PlannedAtUtc.ToUnixTimeSeconds()))
            .Select(item => (JsonElement?)item.Clone())
            .FirstOrDefault();
        if (!nearest.HasValue)
        {
            return null;
        }

        var maxDistance = step == "15min" ? TimeSpan.FromMinutes(30) : TimeSpan.FromMinutes(90);
        var forecastAt = DateTimeOffset.FromUnixTimeSeconds(nearest.Value.GetProperty("dt").GetInt64());
        return (forecastAt - request.PlannedAtUtc).Duration() <= maxDistance ? nearest : null;
    }

    private async Task<IReadOnlyCollection<TripWeatherOfficialAlert>> LoadAlertsAsync(
        JsonElement record,
        CancellationToken cancellationToken)
    {
        if (!record.TryGetProperty("alerts", out JsonElement alerts)
            || alerts.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var result = new List<TripWeatherOfficialAlert>();
        foreach (JsonElement item in alerts.EnumerateArray())
        {
            var id = item.ValueKind == JsonValueKind.String
                ? item.GetString()
                : item.TryGetProperty("id", out JsonElement idElement) ? idElement.GetString() : null;
            if (string.IsNullOrWhiteSpace(id))
            {
                continue;
            }

            using JsonDocument detail = await GetJsonAsync(
                $"data/4.0/onecall/alert/{Uri.EscapeDataString(id)}?appid={Uri.EscapeDataString(options.ApiKey!)}",
                cancellationToken);
            JsonElement root = detail.RootElement;
            var label = ReadString(root, "event");
            if (string.IsNullOrWhiteSpace(label)
                && root.TryGetProperty("tags", out JsonElement tags)
                && tags.ValueKind == JsonValueKind.Array)
            {
                label = tags.EnumerateArray().Select(tag => tag.GetString())
                    .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
            }

            result.Add(new TripWeatherOfficialAlert(
                id,
                string.IsNullOrWhiteSpace(label) ? "Weather alert" : label,
                ReadString(root, "description"),
                ReadUnixTime(root, "start"),
                ReadUnixTime(root, "end")));
        }

        return result;
    }

    private async Task<JsonDocument> GetJsonAsync(string relativeUri, CancellationToken cancellationToken)
    {
        using HttpResponseMessage response = await httpClient.GetAsync(relativeUri, cancellationToken);
        response.EnsureSuccessStatusCode();
        await using Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
    }

    private static string Format(decimal value) => value.ToString(CultureInfo.InvariantCulture);

    private static double ReadRain(JsonElement record) =>
        record.TryGetProperty("rain", out JsonElement rain)
        && rain.TryGetProperty("1h", out JsonElement oneHour)
        && oneHour.TryGetDouble(out double value)
            ? value
            : 0;

    private static bool ReadThunderstorm(JsonElement record) =>
        record.TryGetProperty("weather", out JsonElement weather)
        && weather.ValueKind == JsonValueKind.Array
        && weather.EnumerateArray().Any(item =>
            item.TryGetProperty("id", out JsonElement id) && id.GetInt32() is >= 200 and < 300);

    private static string? ReadCondition(JsonElement record)
    {
        if (!record.TryGetProperty("weather", out JsonElement weather)
            || weather.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        JsonElement first = weather.EnumerateArray().FirstOrDefault();
        return first.ValueKind == JsonValueKind.Undefined ? null : ReadString(first, "description");
    }

    private static string? ReadString(JsonElement root, string property) =>
        root.TryGetProperty(property, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static DateTimeOffset ReadUnixTime(JsonElement root, string property) =>
        root.TryGetProperty(property, out JsonElement value) && value.TryGetInt64(out long seconds)
            ? DateTimeOffset.FromUnixTimeSeconds(seconds)
            : DateTimeOffset.MinValue;
}