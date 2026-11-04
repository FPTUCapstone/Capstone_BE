namespace TripMate.Application.Common.Interfaces;

public sealed record TomorrowNotificationItem(
    string Id,
    string? EventId,
    string? AlertId,
    string? Type,
    DateTimeOffset? UpdateTime,
    string RawJson);

public sealed record TomorrowNotificationsResponse(
    IReadOnlyList<TomorrowNotificationItem> Notifications,
    string? NextPageToken,
    bool ProviderUnavailable = false);

public interface ITomorrowAlertsClient
{
    Task<TomorrowNotificationsResponse> GetNotificationsAsync(
        DateTimeOffset startTime,
        DateTimeOffset endTime,
        string? pageToken,
        CancellationToken cancellationToken = default);

    Task<string> CreateLocationAsync(
        string name,
        double latitude,
        double longitude,
        CancellationToken cancellationToken = default);

    Task DeleteLocationAsync(
        string locationId,
        CancellationToken cancellationToken = default);
}
