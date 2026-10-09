namespace TripMate.Domain.Entities;

/// <summary>Read model for an ingested weather event in trip.WeatherEvents (UC-59).</summary>
public sealed class WeatherEvent
{
    public const string SeverityLow = "Low";
    public const string SeverityModerate = "Moderate";
    public const string SeveritySevere = "Severe";
    public const string SeverityExtreme = "Extreme";

    private WeatherEvent()
    {
    }

    public long Id { get; private set; }

    public string? RegionName { get; private set; }

    public decimal? Latitude { get; private set; }

    public decimal? Longitude { get; private set; }

    public string EventType { get; private set; } = string.Empty;

    public string Severity { get; private set; } = string.Empty;

    public string? Description { get; private set; }

    public DateTimeOffset ValidFromUtc { get; private set; }

    public DateTimeOffset? ValidToUtc { get; private set; }

    public string Source { get; private set; } = string.Empty;

    public DateTimeOffset IngestedAtUtc { get; private set; }
}