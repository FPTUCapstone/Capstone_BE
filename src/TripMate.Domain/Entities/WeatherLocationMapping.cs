namespace TripMate.Domain.Entities;

/// <summary>
/// One TripMate point (meeting point or weather-required stop) registered as a Tomorrow.io
/// Location and linked to the configured Alert while a departure still needs it.
/// </summary>
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
        DateTimeOffset lastDepartureEndAtUtc,
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
            LastDepartureEndAtUtc = lastDepartureEndAtUtc.ToUniversalTime(),
            IsActive = true,
            CreatedAtUtc = nowUtc.ToUniversalTime(),
            UpdatedAtUtc = nowUtc.ToUniversalTime(),
        };
    }

    /// <summary>Keeps monitoring until the latest departure that needs this point has ended.</summary>
    public void ExtendMonitoring(DateTimeOffset departureEndAtUtc, DateTimeOffset nowUtc)
    {
        if (LastDepartureEndAtUtc.HasValue && departureEndAtUtc <= LastDepartureEndAtUtc.Value)
        {
            return;
        }

        LastDepartureEndAtUtc = departureEndAtUtc.ToUniversalTime();
        UpdatedAtUtc = nowUtc.ToUniversalTime();
    }

    /// <summary>A retired point needed again gets a fresh Tomorrow.io Location.</summary>
    public void Reactivate(string tomorrowLocationId, DateTimeOffset departureEndAtUtc, DateTimeOffset nowUtc)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tomorrowLocationId);
        TomorrowLocationId = tomorrowLocationId.Trim();
        LastDepartureEndAtUtc = departureEndAtUtc.ToUniversalTime();
        IsActive = true;
        UpdatedAtUtc = nowUtc.ToUniversalTime();
    }

    public void Deactivate(DateTimeOffset nowUtc)
    {
        IsActive = false;
        UpdatedAtUtc = nowUtc.ToUniversalTime();
    }
}
