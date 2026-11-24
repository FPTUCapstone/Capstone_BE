namespace TripMate.Domain.Entities;

/// <summary>Outcome of a proposal state change (UC-15, spec 3.1).</summary>
public enum ReroutingTransition
{
    /// <summary>The proposal was open and is now in the requested state.</summary>
    Applied = 1,

    /// <summary>The proposal was already in the requested state; nothing changed (double tap / retry).</summary>
    Replayed = 2,

    /// <summary>The proposal is closed in a different state; nothing changed.</summary>
    Closed = 3,
}

/// <summary>
/// A rerouting proposal in trip.ReroutingEvents. Proposed by the Rerouting Engine (UC-15) and then
/// accepted, rejected or expired; every transition leaves <c>Proposed</c> exactly once.
/// </summary>
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

    public static ReroutingEvent Propose(
        Incident incident,
        TripSession session,
        string proposedItinerarySnapshot,
        DateTimeOffset proposedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(incident);
        ArgumentNullException.ThrowIfNull(session);
        if (string.IsNullOrWhiteSpace(proposedItinerarySnapshot))
        {
            throw new ArgumentException("A proposal snapshot is required.", nameof(proposedItinerarySnapshot));
        }

        EnsureUtc(proposedAtUtc, nameof(proposedAtUtc));
        if (!ReferenceEquals(incident.Session, session) && (incident.SessionId <= 0 || incident.SessionId != session.Id))
        {
            throw new ArgumentException("The incident belongs to another session.", nameof(incident));
        }

        // Navigation properties, not scalar ids: both may still be unsaved (AGENTS 3.2).
        return new ReroutingEvent
        {
            Incident = incident,
            Session = session,
            ProposedItinerarySnapshot = proposedItinerarySnapshot,
            ProposedAtUtc = proposedAtUtc,
        };
    }

    /// <summary>
    /// Whether the Traveler's window has passed: <c>now &gt; proposed_at + ttl</c>, strictly. At exactly
    /// <c>proposed_at + ttl</c> the proposal is still open (SRS 6.a1, spec 2.2).
    /// </summary>
    public bool IsExpiredAt(DateTimeOffset nowUtc, TimeSpan ttl) => nowUtc > ProposedAtUtc + ttl;

    public ReroutingTransition Accept(DateTimeOffset nowUtc) => Decide(StatusAccepted, nowUtc);

    public ReroutingTransition Reject(DateTimeOffset nowUtc) => Decide(StatusRejected, nowUtc);

    public ReroutingTransition Expire(DateTimeOffset nowUtc) => Decide(StatusExpired, nowUtc);

    private ReroutingTransition Decide(string target, DateTimeOffset nowUtc)
    {
        EnsureUtc(nowUtc, nameof(nowUtc));
        if (Status == target)
        {
            return ReroutingTransition.Replayed;
        }

        if (Status != StatusProposed)
        {
            return ReroutingTransition.Closed;
        }

        Status = target;
        DecidedAtUtc = nowUtc;
        return ReroutingTransition.Applied;
    }

    private static void EnsureUtc(DateTimeOffset value, string parameterName)
    {
        if (value.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("The timestamp must be UTC.", parameterName);
        }
    }
}