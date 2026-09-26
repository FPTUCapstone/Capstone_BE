using TripMate.Domain.Common;

namespace TripMate.Domain.Entities;

/// <summary>Read model for an executing trip in trip.TripSessions.</summary>
public sealed class TripSession : BaseEntity
{
    public const string PlanningState = "Planning";
    public const string NavigatingState = "Navigating";
    public const string ExploringState = "Exploring";
    public const string InterruptedState = "Interrupted";
    public const string CompletedState = "Completed";

    private TripSession()
    {
    }

    public long ItineraryId { get; private set; }
    public Itinerary Itinerary { get; private set; } = null!;
    public long TravelerUserId { get; private set; }
    public User TravelerUser { get; private set; } = null!;
    public string FsmState { get; private set; } = PlanningState;
    public decimal? CurrentLatitude { get; private set; }
    public decimal? CurrentLongitude { get; private set; }
    public DateTimeOffset? StartedAtUtc { get; private set; }
    public DateTimeOffset? EndedAtUtc { get; private set; }
    public DateTimeOffset? LastSyncedAtUtc { get; private set; }
}