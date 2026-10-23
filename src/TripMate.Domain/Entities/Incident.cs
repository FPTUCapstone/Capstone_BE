using TripMate.Domain.Common;

namespace TripMate.Domain.Entities;

/// <summary>Read model for an operational alert in trip.Incidents.</summary>
public sealed class Incident : BaseEntity
{
    public const int SourceKeyMaxLength = 300;
    public const int DescriptionMaxLength = 500;
    public const string SevereWeatherType = "SevereWeather";
    public const string ScheduleDelayType = "ScheduleDelay";
    public const string RouteDeviationType = "RouteDeviation";
    public const string PoiClosureType = "POIClosure";

    private Incident()
    {
    }

    public long SessionId { get; private set; }
    public TripSession Session { get; private set; } = null!;
    public string IncidentType { get; private set; } = string.Empty;
    public long? WeatherEventId { get; private set; }
    public string? Description { get; private set; }
    public long? AffectedItineraryItemId { get; private set; }
    public TripSessionItem? AffectedItem { get; private set; }
    public string? SourceKey { get; private set; }
    public Guid? ClientEventId { get; private set; }
    public DateTimeOffset DetectedAtUtc { get; private set; }
    public DateTimeOffset? ResolvedAtUtc { get; private set; }

    public static Incident CreateTripAlert(
        TripSession session,
        string incidentType,
        string sourceKey,
        DateTimeOffset detectedAtUtc,
        long? affectedItineraryItemId = null,
        Guid? clientEventId = null,
        string? description = null)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (incidentType is not SevereWeatherType
            and not ScheduleDelayType
            and not RouteDeviationType
            and not PoiClosureType)
        {
            throw new ArgumentOutOfRangeException(nameof(incidentType));
        }

        var normalizedSourceKey = Required(sourceKey, SourceKeyMaxLength, nameof(sourceKey));
        var normalizedDescription = Optional(description, DescriptionMaxLength, nameof(description));
        if (clientEventId == Guid.Empty)
        {
            throw new ArgumentException("Client event ID cannot be empty.", nameof(clientEventId));
        }

        return new Incident
        {
            Session = session,
            IncidentType = incidentType,
            AffectedItineraryItemId = affectedItineraryItemId,
            AffectedItem = affectedItineraryItemId.HasValue
                ? session.Items.SingleOrDefault(item =>
                    item.ItineraryItemId == affectedItineraryItemId.Value)
                : null,
            SourceKey = normalizedSourceKey,
            ClientEventId = clientEventId,
            Description = normalizedDescription,
            DetectedAtUtc = detectedAtUtc.ToUniversalTime(),
        };
    }

    public bool Resolve(DateTimeOffset resolvedAtUtc)
    {
        if (ResolvedAtUtc.HasValue)
        {
            return false;
        }

        ResolvedAtUtc = resolvedAtUtc.ToUniversalTime();
        return true;
    }

    private static string Required(string value, int maxLength, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("Value is required.", parameterName);
        }

        var normalized = value.Trim();
        if (normalized.Length > maxLength)
        {
            throw new ArgumentException($"Value cannot exceed {maxLength} characters.", parameterName);
        }

        return normalized;
    }

    private static string? Optional(string? value, int maxLength, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var normalized = value.Trim();
        if (normalized.Length > maxLength)
        {
            throw new ArgumentException($"Value cannot exceed {maxLength} characters.", parameterName);
        }

        return normalized;
    }
}