using System.Text.Json;
using System.Text.RegularExpressions;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Common.Models;
using TripMate.Application.Features.Weather.Emergency;

namespace TripMate.Infrastructure.Weather;

public sealed class TomorrowAlertNormalizer : IWeatherAlertNormalizer
{
    public const string ProviderName = "TomorrowIo";

    private static readonly Regex RiskLevelRegex = new(
        @"(?:rủi ro(?:\s+thiên\s+tai)?\s+cấp|risk\s+level)\s*([1-5])",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public bool CanHandle(string provider) =>
        string.Equals(provider, ProviderName, StringComparison.OrdinalIgnoreCase);

    public Result<IReadOnlyList<NormalizedWeatherEvent>> Normalize(
        string rawPayload,
        string provider,
        string? providerNotificationId,
        DateTimeOffset receivedAtUtc)
    {
        if (string.IsNullOrWhiteSpace(rawPayload))
        {
            return Result<IReadOnlyList<NormalizedWeatherEvent>>.Failure(
                "Weather.PayloadEmpty", "Payload cannot be empty.");
        }

        try
        {
            using var doc = JsonDocument.Parse(rawPayload);
            var root = doc.RootElement;
            var eventsList = new List<NormalizedWeatherEvent>();

            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("events", out var eventsArray) && eventsArray.ValueKind == JsonValueKind.Array)
            {
                foreach (var element in eventsArray.EnumerateArray())
                {
                    if (TryParseEvent(element, providerNotificationId, receivedAtUtc, out var normalized))
                    {
                        eventsList.Add(normalized!);
                    }
                }
            }
            else if (root.ValueKind == JsonValueKind.Array)
            {
                foreach (var element in root.EnumerateArray())
                {
                    if (TryParseEvent(element, providerNotificationId, receivedAtUtc, out var normalized))
                    {
                        eventsList.Add(normalized!);
                    }
                }
            }
            else if (root.ValueKind == JsonValueKind.Object)
            {
                if (TryParseEvent(root, providerNotificationId, receivedAtUtc, out var normalized))
                {
                    eventsList.Add(normalized!);
                }
            }

            return Result<IReadOnlyList<NormalizedWeatherEvent>>.Success(eventsList);
        }
        catch (JsonException ex)
        {
            return Result<IReadOnlyList<NormalizedWeatherEvent>>.Failure(
                "Weather.PayloadMalformed", $"Malformed JSON payload: {ex.Message}");
        }
    }

    private static bool TryParseEvent(
        JsonElement element,
        string? providerNotificationId,
        DateTimeOffset receivedAtUtc,
        out NormalizedWeatherEvent? normalized)
    {
        normalized = null;

        // If nested inside an "event" property
        var target = element;
        if (element.TryGetProperty("event", out var innerEvent) && innerEvent.ValueKind == JsonValueKind.Object)
        {
            target = innerEvent;
        }

        // 1. Event ID
        string? eventId = GetString(target, "id") ?? GetString(element, "id")
            ?? GetString(target, "eventId") ?? GetString(element, "eventId")
            ?? GetString(target, "object_key") ?? GetString(element, "object_key");

        if (string.IsNullOrWhiteSpace(eventId))
        {
            eventId = providerNotificationId ?? Guid.NewGuid().ToString("N");
        }

        // 2. Custom event check
        bool isCustom = element.TryGetProperty("insightId", out _) ||
                        target.TryGetProperty("insightId", out _) ||
                        element.TryGetProperty("feedId", out _) ||
                        target.TryGetProperty("feedId", out _) ||
                        GetBool(element, "isCustomEvent") ||
                        GetBool(target, "isCustomEvent");

        // 3. Timestamps
        DateTimeOffset start = GetDateTimeOffset(target, "start") ?? GetDateTimeOffset(target, "startTime") ?? receivedAtUtc;
        DateTimeOffset end = GetDateTimeOffset(target, "end") ?? GetDateTimeOffset(target, "endTime") ?? start.AddHours(24);
        DateTimeOffset updateTime = GetDateTimeOffset(target, "updateTime") ?? GetDateTimeOffset(target, "updatedAt") ?? receivedAtUtc;

        // 4. Status / Withdrawal
        string? status = GetString(target, "status") ?? GetString(element, "status");
        string? action = GetString(target, "action") ?? GetString(element, "action");
        bool isWithdrawn = string.Equals(status, "Withdrawn", StringComparison.OrdinalIgnoreCase) ||
                           string.Equals(status, "Canceled", StringComparison.OrdinalIgnoreCase) ||
                           string.Equals(status, "Cancelled", StringComparison.OrdinalIgnoreCase) ||
                           string.Equals(action, "CANCEL", StringComparison.OrdinalIgnoreCase) ||
                           string.Equals(action, "WITHDRAW", StringComparison.OrdinalIgnoreCase);

        // 5. Provider severity
        string? providerSeverity = GetString(target, "severity") ?? GetString(element, "severity");

        // 6. Text analysis (Headline, description, instruction, source)
        string headline = GetString(target, "headline") ?? string.Empty;
        string description = GetString(target, "description") ?? string.Empty;
        string instruction = GetString(target, "instruction") ?? string.Empty;
        string source = GetString(target, "source") ?? GetString(target, "authority") ?? string.Empty;

        string combinedText = $"{source} {headline} {description} {instruction}";

        // Authority extraction (match longest specific name first)
        string? detectedAuthority = null;
        foreach (var authority in VietnameseWeatherAuthorities.DefaultAllowlist.OrderByDescending(a => a.Length))
        {
            if (combinedText.Contains(authority, StringComparison.OrdinalIgnoreCase))
            {
                detectedAuthority = authority;
                break;
            }
        }

        // Vietnamese risk level (1-5)
        int? officialRiskLevel = null;
        var match = RiskLevelRegex.Match(combinedText);
        if (match.Success && int.TryParse(match.Groups[1].Value, out int level))
        {
            officialRiskLevel = level;
        }

        // Directive extraction (only if allowlisted and not custom event)
        var directive = WeatherVerifiedDirective.None;
        if (!isCustom && detectedAuthority is not null)
        {
            if (combinedText.Contains("cấm biển", StringComparison.OrdinalIgnoreCase) ||
                combinedText.Contains("sea ban", StringComparison.OrdinalIgnoreCase) ||
                combinedText.Contains("cấm tàu", StringComparison.OrdinalIgnoreCase) ||
                combinedText.Contains("cấm lưu thông", StringComparison.OrdinalIgnoreCase) ||
                combinedText.Contains("cấm đường", StringComparison.OrdinalIgnoreCase) ||
                combinedText.Contains("road closure", StringComparison.OrdinalIgnoreCase))
            {
                directive = WeatherVerifiedDirective.TravelProhibition;
            }
            else if (combinedText.Contains("sơ tán", StringComparison.OrdinalIgnoreCase) ||
                     combinedText.Contains("evacuation", StringComparison.OrdinalIgnoreCase))
            {
                directive = WeatherVerifiedDirective.Evacuation;
            }
            else if (combinedText.Contains("đóng cửa", StringComparison.OrdinalIgnoreCase) ||
                     combinedText.Contains("attraction closure", StringComparison.OrdinalIgnoreCase) ||
                     combinedText.Contains("tạm dừng hoạt động", StringComparison.OrdinalIgnoreCase))
            {
                directive = WeatherVerifiedDirective.Closure;
            }
        }

        // 7. Geometry parsing
        var areas = ParseAreas(target);
        if (areas.Count == 0 && element.TryGetProperty("geometry", out _))
        {
            areas = ParseAreas(element);
        }

        normalized = new NormalizedWeatherEvent(
            Provider: ProviderName,
            ProviderEventId: eventId,
            UpdatedAtUtc: updateTime,
            ValidFromUtc: start,
            ValidToUtc: end,
            Areas: areas,
            ReceivedAtUtc: receivedAtUtc)
        {
            ProviderNotificationId = providerNotificationId,
            Revision = updateTime.ToUnixTimeSeconds().ToString(),
            OriginalAuthority = detectedAuthority,
            OriginalSourceReference = string.IsNullOrWhiteSpace(source) ? null : source,
            Description = string.IsNullOrWhiteSpace(description) ? null : description,
            Category = GetString(target, "eventCategory") ?? GetString(target, "category"),
            OfficialVietnamRiskLevel = officialRiskLevel,
            VerifiedDirective = directive,
            IsWithdrawn = isWithdrawn,
            ProviderSeverity = providerSeverity,
            IsCustomEvent = isCustom
        };

        return true;
    }

    private static IReadOnlyList<WeatherPolygon> ParseAreas(JsonElement container)
    {
        var areas = new List<WeatherPolygon>();

        if (container.TryGetProperty("geometry", out var geometry))
        {
            AddPolygonFromGeometry(geometry, areas);
        }
        else if (container.TryGetProperty("locations", out var locations) && locations.ValueKind == JsonValueKind.Array)
        {
            foreach (var loc in locations.EnumerateArray())
            {
                if (loc.TryGetProperty("geojsonGeometry", out var locGeom))
                {
                    AddPolygonFromGeometry(locGeom, areas);
                }
            }
        }

        return areas;
    }

    private static void AddPolygonFromGeometry(JsonElement geometry, List<WeatherPolygon> areas)
    {
        if (!geometry.TryGetProperty("type", out var typeProp)) return;
        string? geomType = typeProp.GetString();

        if (string.Equals(geomType, "Polygon", StringComparison.OrdinalIgnoreCase) &&
            geometry.TryGetProperty("coordinates", out var coords) &&
            coords.ValueKind == JsonValueKind.Array)
        {
            var rings = new List<IReadOnlyList<WeatherPoint>>();
            foreach (var ringElement in coords.EnumerateArray())
            {
                if (ringElement.ValueKind != JsonValueKind.Array) continue;
                var ringCoords = new List<WeatherPoint>();
                foreach (var posElement in ringElement.EnumerateArray())
                {
                    if (posElement.ValueKind == JsonValueKind.Array && posElement.GetArrayLength() >= 2)
                    {
                        decimal lon = (decimal)posElement[0].GetDouble();
                        decimal lat = (decimal)posElement[1].GetDouble();
                        ringCoords.Add(new WeatherPoint(lat, lon));
                    }
                }
                if (ringCoords.Count > 0)
                {
                    rings.Add(ringCoords);
                }
            }
            if (rings.Count > 0)
            {
                var outer = rings[0];
                var holes = rings.Skip(1).ToList();
                areas.Add(new WeatherPolygon(outer) { Holes = holes });
            }
        }
    }

    private static string? GetString(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var prop) && prop.ValueKind == JsonValueKind.String
            ? prop.GetString()
            : null;

    private static bool GetBool(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var prop) &&
        (prop.ValueKind == JsonValueKind.True || (prop.ValueKind == JsonValueKind.String && bool.TryParse(prop.GetString(), out bool b) && b));

    private static DateTimeOffset? GetDateTimeOffset(JsonElement element, string propertyName)
    {
        if (element.TryGetProperty(propertyName, out var prop) && prop.ValueKind == JsonValueKind.String)
        {
            if (DateTimeOffset.TryParse(prop.GetString(), out var dto))
            {
                return dto.ToUniversalTime();
            }
        }
        return null;
    }
}
