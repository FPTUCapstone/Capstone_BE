using TripMate.Domain.Common;

namespace TripMate.Domain.Entities;

public sealed class TripStateHistory : BaseEntity
{
    public const string NavigationStartedReason = "NavigationStarted";
    public const string ItemReachedReason = "ItemReached";
    public const string TravelerDepartedReason = "TravelerDeparted";
    public const string TravelerTrigger = "Traveler";
    public const string SystemTrigger = "System";

    private TripStateHistory()
    {
    }

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