namespace TripMate.Application.Common.Interfaces;

/// <summary>How a Tomorrow.io call ended, so callers can stop instead of hammering the provider.</summary>
public enum TomorrowCallOutcome
{
    Success,

    /// <summary>404: the Alert or Location does not exist (for unlink/delete: already gone).</summary>
    NotFound,

    /// <summary>401/403: bad credentials or the account is not entitled. Needs an operator fix, not a retry.</summary>
    NotAuthorized,

    /// <summary>429, 5xx, network failure or timeout: retry on the next background cycle.</summary>
    Unavailable,

    /// <summary>A 2xx answer that does not match the documented contract.</summary>
    InvalidResponse,

    /// <summary>Integration disabled or not configured; nothing was sent.</summary>
    Skipped,
}

public sealed record TomorrowNotificationItem(
    string Id,
    string? AlertId,
    string RawJson);

public sealed record TomorrowNotificationsResponse(
    IReadOnlyList<TomorrowNotificationItem> Notifications,
    string? NextPageToken,
    bool ProviderUnavailable = false);

public sealed record TomorrowCreatedLocation(TomorrowCallOutcome Outcome, string? LocationId);

public sealed record TomorrowLinkedLocations(TomorrowCallOutcome Outcome, IReadOnlySet<string> LocationIds);

/// <summary>
/// Tomorrow.io v4 calls UC-70 needs. Every Alert call targets the configured
/// <c>TomorrowIo:AlertId</c>; TripMate never creates Insights or Alerts.
/// </summary>
public interface ITomorrowAlertsClient
{
    /// <summary>GET /v4/notifications filtered by the configured Alert.</summary>
    Task<TomorrowNotificationsResponse> GetNotificationsAsync(
        DateTimeOffset startTime,
        DateTimeOffset endTime,
        string? pageToken,
        CancellationToken cancellationToken = default);

    /// <summary>POST /v4/locations.</summary>
    Task<TomorrowCreatedLocation> CreateLocationAsync(
        string name,
        double latitude,
        double longitude,
        CancellationToken cancellationToken = default);

    /// <summary>DELETE /v4/locations/{locationId}.</summary>
    Task<TomorrowCallOutcome> DeleteLocationAsync(
        string locationId,
        CancellationToken cancellationToken = default);

    /// <summary>GET /v4/alerts/{alertId}/locations.</summary>
    Task<TomorrowLinkedLocations> GetLinkedLocationIdsAsync(CancellationToken cancellationToken = default);

    /// <summary>POST /v4/alerts/{alertId}/locations/link with <c>{"locations":[...]}</c>.</summary>
    Task<TomorrowCallOutcome> LinkLocationsAsync(
        IReadOnlyCollection<string> locationIds,
        CancellationToken cancellationToken = default);

    /// <summary>POST /v4/alerts/{alertId}/locations/unlink with <c>{"locations":[...]}</c>.</summary>
    Task<TomorrowCallOutcome> UnlinkLocationsAsync(
        IReadOnlyCollection<string> locationIds,
        CancellationToken cancellationToken = default);
}
