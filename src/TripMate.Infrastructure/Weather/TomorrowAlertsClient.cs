using System.Net;
using System.Text;
using System.Text.Json;

using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using TripMate.Application.Common.Interfaces;

namespace TripMate.Infrastructure.Weather;

/// <summary>
/// Tomorrow.io v4 Locations, Alert links and Notifications. The key travels in the documented
/// <c>apikey</c> header (https://docs.tomorrow.io/reference/api-authentication), never in a URL,
/// so request logs cannot carry it.
/// </summary>
public sealed class TomorrowAlertsClient(
    HttpClient httpClient,
    IOptions<TomorrowIoOptions> options,
    ILogger<TomorrowAlertsClient> logger) : ITomorrowAlertsClient
{
    private const string NotificationsPath = "/v4/notifications";

    private readonly TomorrowIoOptions _options = options.Value;

    private bool IsConfigured =>
        _options.Enabled && !string.IsNullOrWhiteSpace(_options.ApiKey) && !string.IsNullOrWhiteSpace(_options.AlertId);

    public async Task<TomorrowNotificationsResponse> GetNotificationsAsync(
        DateTimeOffset startTime,
        DateTimeOffset endTime,
        string? pageToken,
        CancellationToken cancellationToken = default)
    {
        if (!IsConfigured)
        {
            logger.LogDebug("Tomorrow.io alerts are disabled or not configured. Skipping notification reconciliation.");
            return new TomorrowNotificationsResponse([], null);
        }

        var query = new Dictionary<string, string?>
        {
            ["startTime"] = startTime.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ"),
            ["endTime"] = endTime.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ"),
            ["alertIds"] = _options.AlertId,
        };

        // pageToken is the documented links.next path; only its paging cursor is reused so the
        // request always stays on this endpoint and keeps TripMate's own filters.
        if (!string.IsNullOrWhiteSpace(pageToken))
        {
            if (!TryReadPagingCursor(pageToken, query))
            {
                logger.LogWarning("Tomorrow.io returned an unexpected notifications next link; stopping pagination.");
                return new TomorrowNotificationsResponse([], null, ProviderUnavailable: true);
            }
        }

        using var request = CreateRequest(HttpMethod.Get, QueryHelpers.AddQueryString(NotificationsPath, query));
        var (outcome, body) = await SendAsync(request, "ListNotifications", cancellationToken);
        if (outcome != TomorrowCallOutcome.Success)
        {
            return new TomorrowNotificationsResponse([], null, ProviderUnavailable: true);
        }

        try
        {
            using var doc = JsonDocument.Parse(body);
            if (!doc.RootElement.TryGetProperty("data", out var data)
                || data.ValueKind != JsonValueKind.Object
                || !data.TryGetProperty("notifications", out var notifications)
                || notifications.ValueKind != JsonValueKind.Array)
            {
                logger.LogWarning("Tomorrow.io notifications response does not carry data.notifications.");
                return new TomorrowNotificationsResponse([], null, ProviderUnavailable: true);
            }

            var items = new List<TomorrowNotificationItem>();
            foreach (var element in notifications.EnumerateArray())
            {
                string? id = GetString(element, "id");
                if (string.IsNullOrWhiteSpace(id))
                {
                    continue;
                }

                string? alertId = element.TryGetProperty("alert", out var alert) ? GetString(alert, "id") : null;
                items.Add(new TomorrowNotificationItem(id, alertId, element.GetRawText()));
            }

            string? next = doc.RootElement.TryGetProperty("links", out var links) ? GetString(links, "next") : null;
            return new TomorrowNotificationsResponse(items, items.Count == 0 ? null : next);
        }
        catch (JsonException)
        {
            logger.LogWarning("Tomorrow.io notifications response is not valid JSON.");
            return new TomorrowNotificationsResponse([], null, ProviderUnavailable: true);
        }
    }

    public async Task<TomorrowCreatedLocation> CreateLocationAsync(
        string name,
        double latitude,
        double longitude,
        CancellationToken cancellationToken = default)
    {
        if (!IsConfigured)
        {
            return new TomorrowCreatedLocation(TomorrowCallOutcome.Skipped, null);
        }

        using var request = CreateRequest(HttpMethod.Post, "/v4/locations", new
        {
            name,
            geometry = new { type = "Point", coordinates = new[] { longitude, latitude } },
        });
        var (outcome, body) = await SendAsync(request, "CreateLocation", cancellationToken);
        if (outcome != TomorrowCallOutcome.Success)
        {
            return new TomorrowCreatedLocation(outcome, null);
        }

        try
        {
            using var doc = JsonDocument.Parse(body);
            string? id = doc.RootElement.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object
                && data.TryGetProperty("location", out var location)
                ? GetString(location, "id")
                : null;
            if (string.IsNullOrWhiteSpace(id))
            {
                logger.LogWarning("Tomorrow.io CreateLocation answered without data.location.id.");
                return new TomorrowCreatedLocation(TomorrowCallOutcome.InvalidResponse, null);
            }

            return new TomorrowCreatedLocation(TomorrowCallOutcome.Success, id);
        }
        catch (JsonException)
        {
            logger.LogWarning("Tomorrow.io CreateLocation response is not valid JSON.");
            return new TomorrowCreatedLocation(TomorrowCallOutcome.InvalidResponse, null);
        }
    }

    public async Task<TomorrowCallOutcome> DeleteLocationAsync(
        string locationId,
        CancellationToken cancellationToken = default)
    {
        if (!IsConfigured || string.IsNullOrWhiteSpace(locationId))
        {
            return TomorrowCallOutcome.Skipped;
        }

        using var request = CreateRequest(HttpMethod.Delete, $"/v4/locations/{Uri.EscapeDataString(locationId)}");
        return (await SendAsync(request, "DeleteLocation", cancellationToken)).Outcome;
    }

    public async Task<TomorrowLinkedLocations> GetLinkedLocationIdsAsync(CancellationToken cancellationToken = default)
    {
        if (!IsConfigured)
        {
            return new TomorrowLinkedLocations(TomorrowCallOutcome.Skipped, new HashSet<string>());
        }

        using var request = CreateRequest(HttpMethod.Get, $"{AlertPath()}/locations");
        var (outcome, body) = await SendAsync(request, "ListLinkedLocations", cancellationToken);
        if (outcome != TomorrowCallOutcome.Success)
        {
            return new TomorrowLinkedLocations(outcome, new HashSet<string>());
        }

        try
        {
            using var doc = JsonDocument.Parse(body);
            if (!doc.RootElement.TryGetProperty("data", out var data)
                || data.ValueKind != JsonValueKind.Object
                || !data.TryGetProperty("locations", out var locations)
                || locations.ValueKind != JsonValueKind.Array)
            {
                logger.LogWarning("Tomorrow.io linked locations response does not carry data.locations.");
                return new TomorrowLinkedLocations(TomorrowCallOutcome.InvalidResponse, new HashSet<string>());
            }

            var ids = locations.EnumerateArray()
                .Where(location => location.ValueKind == JsonValueKind.String)
                .Select(location => location.GetString()!)
                .ToHashSet(StringComparer.Ordinal);
            return new TomorrowLinkedLocations(TomorrowCallOutcome.Success, ids);
        }
        catch (JsonException)
        {
            logger.LogWarning("Tomorrow.io linked locations response is not valid JSON.");
            return new TomorrowLinkedLocations(TomorrowCallOutcome.InvalidResponse, new HashSet<string>());
        }
    }

    public Task<TomorrowCallOutcome> LinkLocationsAsync(
        IReadOnlyCollection<string> locationIds,
        CancellationToken cancellationToken = default) =>
        ChangeLinksAsync("link", "LinkLocations", locationIds, cancellationToken);

    public Task<TomorrowCallOutcome> UnlinkLocationsAsync(
        IReadOnlyCollection<string> locationIds,
        CancellationToken cancellationToken = default) =>
        ChangeLinksAsync("unlink", "UnlinkLocations", locationIds, cancellationToken);

    private async Task<TomorrowCallOutcome> ChangeLinksAsync(
        string pathSegment,
        string operation,
        IReadOnlyCollection<string> locationIds,
        CancellationToken cancellationToken)
    {
        if (!IsConfigured || locationIds.Count == 0)
        {
            return TomorrowCallOutcome.Skipped;
        }

        using var request = CreateRequest(HttpMethod.Post, $"{AlertPath()}/locations/{pathSegment}",
            new { locations = locationIds });
        return (await SendAsync(request, operation, cancellationToken)).Outcome;
    }

    private string AlertPath() => $"/v4/alerts/{Uri.EscapeDataString(_options.AlertId)}";

    private HttpRequestMessage CreateRequest(HttpMethod method, string pathAndQuery, object? body = null)
    {
        var request = new HttpRequestMessage(method, $"{_options.BaseUrl.TrimEnd('/')}{pathAndQuery}");
        request.Headers.Add("apikey", _options.ApiKey);
        if (body is not null)
        {
            request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        }

        return request;
    }

    private async Task<(TomorrowCallOutcome Outcome, string Body)> SendAsync(
        HttpRequestMessage request,
        string operation,
        CancellationToken cancellationToken)
    {
        try
        {
            using var response = await httpClient.SendAsync(request, cancellationToken);
            var outcome = Classify(response.StatusCode);
            if (outcome != TomorrowCallOutcome.Success)
            {
                // Status only: provider bodies and request URLs stay out of logs.
                logger.LogWarning("Tomorrow.io {Operation} returned {StatusCode} ({Outcome}).",
                    operation, (int)response.StatusCode, outcome);
                return (outcome, string.Empty);
            }

            return (outcome, await response.Content.ReadAsStringAsync(cancellationToken));
        }
        catch (HttpRequestException exception)
        {
            logger.LogWarning("Tomorrow.io {Operation} failed on the network: {Error}.", operation, exception.HttpRequestError);
            return (TomorrowCallOutcome.Unavailable, string.Empty);
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning("Tomorrow.io {Operation} timed out.", operation);
            return (TomorrowCallOutcome.Unavailable, string.Empty);
        }
    }

    private static TomorrowCallOutcome Classify(HttpStatusCode statusCode) => statusCode switch
    {
        HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => TomorrowCallOutcome.NotAuthorized,
        HttpStatusCode.NotFound => TomorrowCallOutcome.NotFound,
        HttpStatusCode.TooManyRequests => TomorrowCallOutcome.Unavailable,
        >= HttpStatusCode.InternalServerError => TomorrowCallOutcome.Unavailable,
        >= HttpStatusCode.OK and < HttpStatusCode.MultipleChoices => TomorrowCallOutcome.Success,
        _ => TomorrowCallOutcome.InvalidResponse,
    };

    private static bool TryReadPagingCursor(string nextLink, Dictionary<string, string?> query)
    {
        if (!nextLink.StartsWith(NotificationsPath + "?", StringComparison.Ordinal))
        {
            return false;
        }

        var parameters = QueryHelpers.ParseQuery(nextLink[NotificationsPath.Length..]);
        foreach (string key in new[] { "limit", "marker", "offset" })
        {
            if (parameters.TryGetValue(key, out var value))
            {
                query[key] = value.ToString();
            }
        }

        return parameters.ContainsKey("offset") || parameters.ContainsKey("marker");
    }

    private static string? GetString(JsonElement element, string propertyName) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(propertyName, out var property)
        && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;
}