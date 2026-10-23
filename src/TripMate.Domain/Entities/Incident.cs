using TripMate.Domain.Common;

namespace TripMate.Domain.Entities;

/// <summary>Read model for an operational alert in trip.Incidents.</summary>
public sealed class Incident : BaseEntity
{
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
    public DateTimeOffset DetectedAtUtc { get; private set; }
    public DateTimeOffset? ResolvedAtUtc { get; private set; }
}