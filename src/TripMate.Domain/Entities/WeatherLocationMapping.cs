namespace TripMate.Domain.Entities;

public sealed class WeatherLocationMapping
{
    public const int KeyMaxLength = 100;
    public const int TomorrowIdMaxLength = 100;
    public const int NameMaxLength = 200;

    private WeatherLocationMapping() { }

    public long Id { get; private set; }
    public string TripMateLocationKey { get; private set; } = string.Empty;
    public string TomorrowLocationId { get; private set; } = string.Empty;
    public decimal Latitude { get; private set; }
    public decimal Longitude { get; private set; }
    public string? Name { get; private set; }
    public int LinkedDeparturesCount { get; private set; }
    public DateTimeOffset? LastDepartureEndAtUtc { get; private set; }
    public bool IsActive { get; private set; } = true;
    public DateTimeOffset CreatedAtUtc { get; private set; }
    public DateTimeOffset UpdatedAtUtc { get; private set; }

    public static WeatherLocationMapping Create(
        string tripMateLocationKey,
        string tomorrowLocationId,
        decimal latitude,
        decimal longitude,
        string? name,
        DateTimeOffset nowUtc)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tripMateLocationKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(tomorrowLocationId);

        return new WeatherLocationMapping
        {
            TripMateLocationKey = tripMateLocationKey.Trim(),
            TomorrowLocationId = tomorrowLocationId.Trim(),
            Latitude = latitude,
            Longitude = longitude,
            Name = string.IsNullOrWhiteSpace(name) ? null : name.Trim(),
            LinkedDeparturesCount = 0,
            IsActive = true,
            CreatedAtUtc = nowUtc.ToUniversalTime(),
            UpdatedAtUtc = nowUtc.ToUniversalTime(),
        };
    }

    public void LinkDeparture(DateTimeOffset departureEndAtUtc, DateTimeOffset nowUtc)
    {
        LinkedDeparturesCount++;
        if (!LastDepartureEndAtUtc.HasValue || departureEndAtUtc > LastDepartureEndAtUtc.Value)
        {
            LastDepartureEndAtUtc = departureEndAtUtc.ToUniversalTime();
        }
        IsActive = true;
        UpdatedAtUtc = nowUtc.ToUniversalTime();
    }

    public void UnlinkDeparture(DateTimeOffset nowUtc)
    {
        if (LinkedDeparturesCount > 0)
        {
            LinkedDeparturesCount--;
        }
        UpdatedAtUtc = nowUtc.ToUniversalTime();
    }

    public void Deactivate(DateTimeOffset nowUtc)
    {
        IsActive = false;
        UpdatedAtUtc = nowUtc.ToUniversalTime();
    }
}
