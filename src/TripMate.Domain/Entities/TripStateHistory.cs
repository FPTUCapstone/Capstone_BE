namespace TripMate.Domain.Entities;

/// <summary>
/// An FSM transition in trip.TripStateHistory. Read by UC-59 and written by the UC-13
/// navigation-session aggregate through <see cref="Transition"/>.
/// </summary>
public sealed class TripStateHistory
{
    public const string TriggeredBySystem = "System";
    public const string TriggeredByTraveler = "Traveler";
    public const string TriggeredByAdministrator = "Administrator";
    public const string NavigationStartedReason = "NavigationStarted";
    public const string ItemReachedReason = "ItemReached";
    public const string TravelerDepartedReason = "TravelerDeparted";

    private TripStateHistory()
    {
    }

    public long Id { get; private set; }

    public long SessionId { get; private set; }

    public TripSession Session { get; private set; } = null!;

    public string? FromState { get; private set; }

    public string ToState { get; private set; } = string.Empty;

    public string? Reason { get; private set; }

    public string? TriggeredBy { get; private set; }

    public DateTimeOffset ChangedAtUtc { get; private set; }

    internal static TripStateHistory Transition(
        TripSession session,
        string? fromState,
        string toState,
        string reason,
        string triggeredBy,
        DateTimeOffset changedAtUtc) =>
        new()
        {
            Session = session,
            FromState = fromState,
            ToState = toState,
            Reason = reason,
            TriggeredBy = triggeredBy,
            ChangedAtUtc = changedAtUtc,
        };
}