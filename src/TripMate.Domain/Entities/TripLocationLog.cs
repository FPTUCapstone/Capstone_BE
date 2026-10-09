namespace TripMate.Domain.Entities;

/// <summary>Read model for a synchronized position sample in trip.TripLocationLogs (UC-59).</summary>
public sealed class TripLocationLog
{
    private TripLocationLog()
    {
    }

    public long Id { get; private set; }

    public long SessionId { get; private set; }

    public TripSession Session { get; private set; } = null!;

    public decimal Latitude { get; private set; }

    public decimal Longitude { get; private set; }

    public DateTimeOffset RecordedAtUtc { get; private set; }

    public DateTimeOffset? SyncedAtUtc { get; private set; }

    public bool IsOfflineCaptured { get; private set; }
}