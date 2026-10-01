namespace TripMate.Domain.Entities;

/// <summary>Read model for a rerouting proposal in trip.ReroutingEvents (UC-59).</summary>
public sealed class ReroutingEvent
{
    public const string StatusProposed = "Proposed";
    public const string StatusAccepted = "Accepted";
    public const string StatusRejected = "Rejected";
    public const string StatusExpired = "Expired";

    private ReroutingEvent()
    {
    }

    public long Id { get; private set; }

    public long IncidentId { get; private set; }

    public Incident Incident { get; private set; } = null!;

    public long SessionId { get; private set; }

    public TripSession Session { get; private set; } = null!;

    public string ProposedItinerarySnapshot { get; private set; } = string.Empty;

    public string Status { get; private set; } = StatusProposed;

    public DateTimeOffset ProposedAtUtc { get; private set; }

    public DateTimeOffset? DecidedAtUtc { get; private set; }
}