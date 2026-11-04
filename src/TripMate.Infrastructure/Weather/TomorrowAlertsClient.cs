using System.Net;
using System.Text;
using System.Text.Json;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using TripMate.Application.Common.Interfaces;

namespace TripMate.Infrastructure.Weather;

public sealed class TomorrowAlertsClient(
    HttpClient httpClient,
    IOptions<TomorrowIoOptions> options,
    ILogger<TomorrowAlertsClient> logger) : ITomorrowAlertsClient
{
    private readonly TomorrowIoOptions _options = options.Value;

    public async Task<TomorrowNotificationsResponse> GetNotificationsAsync(
        DateTimeOffset startTime,
        DateTimeOffset endTime,
        string? pageToken,
        CancellationToken cancellationToken = default)
    {
        if (!_options.Enabled)
        {
            logger.LogDebug("Tomorrow.io alert processing is disabled. Skipping GetNotificationsAsync call.");
            return new TomorrowNotificationsResponse([], null);
        }

        if (string.IsNullOrWhiteSpace(_options.ApiKey))
        {
            logger.LogWarning("Tomorrow.io ApiKey is not configured. Skipping GetNotificationsAsync call.");
            return new TomorrowNotificationsResponse([], null);
        }

        var baseUrl = _options.BaseUrl.TrimEnd('/');
        var queryParams = new List<string>
        {
            $"startTime={Uri.EscapeDataString(startTime.ToString("yyyy-MM-ddTHH:mm:ssZ"))}",
            $"endTime={Uri.EscapeDataString(endTime.ToString("yyyy-MM-ddTHH:mm:ssZ"))}",
            $"apikey={Uri.EscapeDataString(_options.ApiKey)}"
        };

        if (!string.IsNullOrWhiteSpace(pageToken))
        {
            queryParams.Add($"pageToken={Uri.EscapeDataString(pageToken)}");
        }

        var requestUrl = $"{baseUrl}/v4/notifications?{string.Join("&", queryParams)}";

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, requestUrl);
            using var response = await httpClient.SendAsync(request, cancellationToken);

            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                logger.LogWarning("Tomorrow.io API rate limit (429) encountered during notification reconciliation.");
                return new TomorrowNotificationsResponse([], null, ProviderUnavailable: true);
            }

            if ((int)response.StatusCode >= 500)
            {
                logger.LogWarning("Tomorrow.io API server error ({StatusCode}) during notification reconciliation.", (int)response.StatusCode);
                return new TomorrowNotificationsResponse([], null, ProviderUnavailable: true);
            }

            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("Tomorrow.io API returned unexpected status {StatusCode} during notification reconciliation.", (int)response.StatusCode);
                return new TomorrowNotificationsResponse([], null, ProviderUnavailable: true);
            }

            var contentStream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var doc = await JsonDocument.ParseAsync(contentStream, cancellationToken: cancellationToken);
            var root = doc.RootElement;

            var items = new List<TomorrowNotificationItem>();
            string? nextPageToken = null;

            if (root.TryGetProperty("nextPageToken", out var nextProp) && nextProp.ValueKind == JsonValueKind.String)
            {
                nextPageToken = nextProp.GetString();
            }

            JsonElement notifArray = default;
            if (root.TryGetProperty("notifications", out var notifProp) && notifProp.ValueKind == JsonValueKind.Array)
            {
                notifArray = notifProp;
            }
            else if (root.TryGetProperty("data", out var dataProp) && dataProp.ValueKind == JsonValueKind.Array)
            {
                notifArray = dataProp;
            }

            if (notifArray.ValueKind == JsonValueKind.Array)
            {
                foreach (var el in notifArray.EnumerateArray())
                {
                    string id = el.TryGetProperty("id", out var idProp) && idProp.ValueKind == JsonValueKind.String
                        ? idProp.GetString()!
                        : Guid.NewGuid().ToString("N");

                    string? eventId = el.TryGetProperty("eventId", out var evtProp) && evtProp.ValueKind == JsonValueKind.String
                        ? evtProp.GetString()
                        : null;

                    string? alertId = el.TryGetProperty("alertId", out var altProp) && altProp.ValueKind == JsonValueKind.String
                        ? altProp.GetString()
                        : null;

                    string? type = el.TryGetProperty("type", out var typeProp) && typeProp.ValueKind == JsonValueKind.String
                        ? typeProp.GetString()
                        : null;

                    DateTimeOffset? updateTime = null;
                    if (el.TryGetProperty("updateTime", out var timeProp) && timeProp.TryGetDateTimeOffset(out var parsedTime))
                    {
                        updateTime = parsedTime;
                    }

                    items.Add(new TomorrowNotificationItem(
                        Id: id,
                        EventId: eventId,
                        AlertId: alertId,
                        Type: type,
                        UpdateTime: updateTime,
                        RawJson: el.GetRawText()));
                }
            }

            return new TomorrowNotificationsResponse(items, nextPageToken);
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "Failed to parse Tomorrow.io notifications JSON response.");
            return new TomorrowNotificationsResponse([], null, ProviderUnavailable: true);
        }
        catch (HttpRequestException ex)
        {
            logger.LogWarning(ex, "Network error occurred when querying Tomorrow.io notifications.");
            return new TomorrowNotificationsResponse([], null, ProviderUnavailable: true);
        }
    }

    public async Task<string> CreateLocationAsync(
        string name,
        double latitude,
        double longitude,
        CancellationToken cancellationToken = default)
    {
        if (!_options.Enabled ||
            string.IsNullOrWhiteSpace(_options.ApiKey))
        {
            return string.Empty;
        }

        var baseUrl = _options.BaseUrl.TrimEnd('/');
        var requestUrl = $"{baseUrl}/v4/locations?apikey={Uri.EscapeDataString(_options.ApiKey)}";

        var payload = new
        {
            name,
            location = new { lat = latitude, lon = longitude }
        };

        var json = JsonSerializer.Serialize(payload);
        using var request = new HttpRequestMessage(HttpMethod.Post, requestUrl)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };

        try
        {
            using var response = await httpClient.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("Tomorrow.io CreateLocation returned status {StatusCode}.", (int)response.StatusCode);
                return string.Empty;
            }

            var contentStream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var doc = await JsonDocument.ParseAsync(contentStream, cancellationToken: cancellationToken);
            var root = doc.RootElement;

            if (root.TryGetProperty("id", out var idProp) && idProp.ValueKind == JsonValueKind.String)
            {
                return idProp.GetString()!;
            }

            if (root.TryGetProperty("data", out var dataProp) && dataProp.TryGetProperty("id", out var dataIdProp) && dataIdProp.ValueKind == JsonValueKind.String)
            {
                return dataIdProp.GetString()!;
            }

            return string.Empty;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to create location '{Name}' in Tomorrow.io.", name);
            return string.Empty;
        }
    }

    public async Task DeleteLocationAsync(
        string locationId,
        CancellationToken cancellationToken = default)
    {
        if (!_options.Enabled ||
            string.IsNullOrWhiteSpace(_options.ApiKey) ||
            string.IsNullOrWhiteSpace(locationId))
        {
            return;
        }

        var baseUrl = _options.BaseUrl.TrimEnd('/');
        var requestUrl = $"{baseUrl}/v4/locations/{Uri.EscapeDataString(locationId)}?apikey={Uri.EscapeDataString(_options.ApiKey)}";

        using var request = new HttpRequestMessage(HttpMethod.Delete, requestUrl);
        try
        {
            using var response = await httpClient.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode && response.StatusCode != HttpStatusCode.NotFound)
            {
                logger.LogWarning("Tomorrow.io DeleteLocation returned status {StatusCode}.", (int)response.StatusCode);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to delete location '{LocationId}' from Tomorrow.io.", locationId);
        }
    }
}
