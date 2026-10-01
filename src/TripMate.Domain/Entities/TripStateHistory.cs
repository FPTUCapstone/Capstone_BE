namespace TripMate.Domain.Entities;

/// <summary>Read model for an FSM transition in trip.TripStateHistory (UC-59).</summary>
public sealed class TripStateHistory
{
    public const string TriggeredBySystem = "System";
    public const string TriggeredByTraveler = "Traveler";
    public const string TriggeredByAdministrator = "Administrator";

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
}