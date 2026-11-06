using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

using Microsoft.Extensions.Options;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Common.Models;
using TripMate.Application.Features.Weather.Emergency;

namespace TripMate.Infrastructure.Weather;

/// <summary>
/// Reads the two documented Tomorrow.io Alerts shapes: the webhook body
/// (<c>data.alert / insight / locations / notification / event</c>,
/// https://docs.tomorrow.io/reference/overview-alerts) and one item of
/// <c>GET /v4/notifications</c> (https://docs.tomorrow.io/reference/list-notifications-get).
/// Only notifications of the configured Alert become evidence.
/// </summary>
public sealed class TomorrowAlertNormalizer(IOptions<TomorrowIoOptions> options) : IWeatherAlertNormalizer
{
    private const string CustomInsightEventType = "CustomInsight";
    private const string PublishType = "PUBLISH";

    // PRIOR/START carry a start time only; the event is treated as lasting this long.
    private static readonly TimeSpan OpenEndedEventHorizon = TimeSpan.FromHours(24);

    private static readonly IReadOnlySet<string> EvidenceTypes =
        new HashSet<string>(StringComparer.Ordinal) { "PRIOR", "START", PublishType };

    private static readonly Regex RiskLevelRegex = new(
        @"(?:rủi ro(?:\s+thiên\s+tai)?\s+cấp|risk\s+level)\s*([1-5])",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex CompactOffsetRegex = new(@"([+-]\d{2})(\d{2})$", RegexOptions.Compiled);

    public bool CanHandle(string provider) =>
        string.Equals(provider, WeatherProviders.TomorrowIo, StringComparison.OrdinalIgnoreCase);

    public Result<IReadOnlyList<NormalizedWeatherEvent>> Normalize(
        string rawPayload,
        string provider,
        string? providerNotificationId,
        DateTimeOffset receivedAtUtc)
    {
        if (string.IsNullOrWhiteSpace(rawPayload))
        {
            return Result<IReadOnlyList<NormalizedWeatherEvent>>.Failure(
                WeatherAlertErrorCodes.PayloadEmpty, "Payload cannot be empty.");
        }

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(rawPayload);
        }
        catch (JsonException)
        {
            return Malformed("Payload is not valid JSON.");
        }

        using (doc)
        {
            if (!TryReadNotification(doc.RootElement, out var notification))
            {
                return Malformed("Payload is not a Tomorrow.io Alerts notification.");
            }

            // Another Alert on the account, an all-clear (END) or an unknown type adds no evidence.
            if (string.IsNullOrWhiteSpace(options.Value.AlertId)
                || !string.Equals(notification.AlertId, options.Value.AlertId, StringComparison.Ordinal)
                || !EvidenceTypes.Contains(notification.Type))
            {
                return Result<IReadOnlyList<NormalizedWeatherEvent>>.Success([]);
            }

            if (notification.StartTime is not { } start)
            {
                return Malformed($"A {notification.Type} notification must carry event.startTime.");
            }

            return Result<IReadOnlyList<NormalizedWeatherEvent>>.Success(
                [ToEvent(notification, start, providerNotificationId, receivedAtUtc)]);
        }
    }

    private static NormalizedWeatherEvent ToEvent(
        TomorrowNotification notification,
        DateTimeOffset start,
        string? providerNotificationId,
        DateTimeOffset receivedAtUtc)
    {
        bool isCategory = notification.Type == PublishType;
        string combinedText = string.Join(' ', notification.Texts);

        string? authority = VietnameseWeatherAuthorities.DefaultAllowlist
            .OrderByDescending(candidate => candidate.Length)
            .FirstOrDefault(candidate => combinedText.Contains(candidate, StringComparison.OrdinalIgnoreCase));

        var riskMatch = RiskLevelRegex.Match(combinedText);
        int? riskLevel = riskMatch.Success ? int.Parse(riskMatch.Groups[1].Value, CultureInfo.InvariantCulture) : null;

        // Directives are read from authority-issued category hazards only, never from custom insights.
        var directive = isCategory && authority is not null ? ReadDirective(combinedText) : WeatherVerifiedDirective.None;

        return new NormalizedWeatherEvent(
            Provider: WeatherProviders.TomorrowIo,
            ProviderEventId: EventId(notification, start),
            UpdatedAtUtc: notification.UpdateTime ?? receivedAtUtc,
            ValidFromUtc: start,
            ValidToUtc: notification.EndTime ?? start.Add(OpenEndedEventHorizon),
            Areas: notification.Areas,
            ReceivedAtUtc: receivedAtUtc)
        {
            ProviderNotificationId = providerNotificationId,
            ProviderLocationIds = notification.LocationIds,
            OriginalAuthority = authority,
            Description = notification.Description,
            Category = isCategory ? Truncate(notification.InsightId, Domain.Entities.WeatherEvent.EventTypeMaxLength) ?? PublishType : CustomInsightEventType,
            OfficialVietnamRiskLevel = riskLevel,
            VerifiedDirective = directive,
            ProviderSeverity = MapSeverity(notification.Severity),
            IsCustomEvent = !isCategory,
        };
    }

    /// <summary>
    /// The payload has no event id. PRIOR and START of one occurrence share alert, locations and
    /// startTime, so they collapse into one event and therefore one review per departure.
    /// </summary>
    private static string EventId(TomorrowNotification notification, DateTimeOffset start)
    {
        string locations = notification.LocationIds.Count == 1
            ? notification.LocationIds[0]
            : Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
                string.Join(',', notification.LocationIds.Order(StringComparer.Ordinal))))).ToLowerInvariant()[..32];
        return $"{notification.AlertId}:{start.ToUnixTimeSeconds()}:{locations}";
    }

    private static bool TryReadNotification(JsonElement root, out TomorrowNotification notification)
    {
        notification = default!;
        if (root.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        // Webhook body: everything under data.
        if (root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object
            && data.TryGetProperty("alert", out var webhookAlert))
        {
            var notice = Child(data, "notification");
            var evt = Child(data, "event");
            var values = Child(evt, "eventValues");
            notification = new TomorrowNotification(
                AlertId: GetString(webhookAlert, "id"),
                Type: GetString(notice, "type") ?? string.Empty,
                InsightId: GetString(Child(data, "insight"), "id"),
                LocationIds: LocationIds(data),
                StartTime: GetTime(evt, "startTime"),
                EndTime: GetTime(evt, "endTime"),
                UpdateTime: GetTime(evt, "updateTime"),
                Severity: GetString(evt, "severity"),
                Description: GetString(notice, "description") ?? GetString(notice, "title")
                    ?? GetString(values, "description") ?? GetString(values, "title"),
                Texts: Texts(GetString(evt, "source"), GetString(values, "origin"), GetString(values, "title"),
                    GetString(values, "description"), GetString(values, "instruction"),
                    GetString(notice, "title"), GetString(notice, "description")),
                Areas: Areas(values));
            return true;
        }

        // GET /v4/notifications item: type at the root, event and insight under alert.
        if (root.TryGetProperty("alert", out var listAlert) && listAlert.ValueKind == JsonValueKind.Object)
        {
            var evt = Child(listAlert, "event");
            var insight = Child(listAlert, "insight");
            notification = new TomorrowNotification(
                AlertId: GetString(listAlert, "id"),
                Type: GetString(root, "type") ?? string.Empty,
                InsightId: GetString(insight, "id"),
                LocationIds: LocationIds(root),
                StartTime: GetTime(evt, "startTime") ?? GetTime(evt, "expectedTime"),
                EndTime: GetTime(evt, "endTime"),
                UpdateTime: GetTime(root, "updatedAt"),
                Severity: GetString(insight, "severity"),
                Description: NullIfEmpty(GetString(root, "description")) ?? GetString(root, "title"),
                Texts: Texts(GetString(listAlert, "title"), GetString(listAlert, "description"),
                    GetString(listAlert, "instructions"), GetString(root, "title"), GetString(root, "description")),
                Areas: []);
            return true;
        }

        return false;
    }

    private static WeatherVerifiedDirective ReadDirective(string text)
    {
        if (ContainsAny(text, "cấm biển", "sea ban", "cấm tàu", "cấm lưu thông", "cấm đường", "road closure"))
            return WeatherVerifiedDirective.TravelProhibition;
        if (ContainsAny(text, "sơ tán", "evacuation"))
            return WeatherVerifiedDirective.Evacuation;
        if (ContainsAny(text, "đóng cửa", "attraction closure", "tạm dừng hoạt động"))
            return WeatherVerifiedDirective.Closure;
        return WeatherVerifiedDirective.None;
    }

    // trip.WeatherEvents.severity allows Low, Moderate, Severe, Extreme only.
    private static string? MapSeverity(string? severity) => severity?.Trim().ToLowerInvariant() switch
    {
        "minor" or "low" => Domain.Entities.WeatherEvent.SeverityLow,
        "moderate" => Domain.Entities.WeatherEvent.SeverityModerate,
        "severe" => Domain.Entities.WeatherEvent.SeveritySevere,
        "extreme" => Domain.Entities.WeatherEvent.SeverityExtreme,
        _ => null,
    };

    private static IReadOnlyList<string> LocationIds(JsonElement container) =>
        container.TryGetProperty("locations", out var locations) && locations.ValueKind == JsonValueKind.Array
            ? locations.EnumerateArray()
                .Select(location => GetString(location, "id"))
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Select(id => id!)
                .Distinct(StringComparer.Ordinal)
                .ToList()
            : [];

    // Category hazards publish their area as GeoJSON in eventValues.location.
    private static IReadOnlyList<WeatherPolygon> Areas(JsonElement eventValues)
    {
        var geometry = Child(eventValues, "location");
        if (GetString(geometry, "type") != "Polygon"
            || !geometry.TryGetProperty("coordinates", out var rings) || rings.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var parsed = rings.EnumerateArray()
            .Where(ring => ring.ValueKind == JsonValueKind.Array)
            .Select(ring => (IReadOnlyList<WeatherPoint>)ring.EnumerateArray()
                .Where(position => position.ValueKind == JsonValueKind.Array && position.GetArrayLength() >= 2
                    && position[0].ValueKind == JsonValueKind.Number && position[1].ValueKind == JsonValueKind.Number)
                .Select(position => new WeatherPoint(position[1].GetDecimal(), position[0].GetDecimal()))
                .ToList())
            .Where(ring => ring.Count > 0)
            .ToList();

        return parsed.Count == 0 ? [] : [new WeatherPolygon(parsed[0]) { Holes = parsed.Skip(1).ToList() }];
    }

    private static DateTimeOffset? GetTime(JsonElement element, string propertyName)
    {
        string? value = GetString(element, propertyName);
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        // The docs write offsets both as Z and as +0000.
        value = CompactOffsetRegex.Replace(value.Trim(), "$1:$2");
        return DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed)
            ? parsed.ToUniversalTime()
            : null;
    }

    private static JsonElement Child(JsonElement element, string propertyName) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(propertyName, out var child)
            ? child
            : default;

    private static string? GetString(JsonElement element, string propertyName) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(propertyName, out var property)
        && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;

    private static IReadOnlyList<string> Texts(params string?[] values) =>
        values.Where(value => !string.IsNullOrWhiteSpace(value)).Select(value => value!).ToList();

    private static bool ContainsAny(string text, params string[] needles) =>
        needles.Any(needle => text.Contains(needle, StringComparison.OrdinalIgnoreCase));

    private static string? NullIfEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    private static string? Truncate(string? value, int length) =>
        value is null ? null : value.Length <= length ? value : value[..length];

    private static Result<IReadOnlyList<NormalizedWeatherEvent>> Malformed(string message) =>
        Result<IReadOnlyList<NormalizedWeatherEvent>>.Failure(WeatherAlertErrorCodes.PayloadMalformed, message);

    private sealed record TomorrowNotification(
        string? AlertId,
        string Type,
        string? InsightId,
        IReadOnlyList<string> LocationIds,
        DateTimeOffset? StartTime,
        DateTimeOffset? EndTime,
        DateTimeOffset? UpdateTime,
        string? Severity,
        string? Description,
        IReadOnlyList<string> Texts,
        IReadOnlyList<WeatherPolygon> Areas);
}
