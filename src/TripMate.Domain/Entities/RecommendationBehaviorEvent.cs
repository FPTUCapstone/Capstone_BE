using TripMate.Domain.Common;
using TripMate.Domain.Enums;

namespace TripMate.Domain.Entities;

/// <summary>
/// An append-only snapshot of an explicit traveler recommendation signal.
/// Captured events do not mutate itineraries or perform recommendation scoring.
/// </summary>
public sealed class RecommendationBehaviorEvent : BaseEntity
{
    private RecommendationBehaviorEvent()
    {
    }

    public long TravelerUserId { get; private set; }

    public User TravelerUser { get; private set; } = null!;

    public long PointOfInterestId { get; private set; }

    public PointOfInterest PointOfInterest { get; private set; } = null!;

    public long? ItineraryId { get; private set; }

    public Itinerary? Itinerary { get; private set; }

    public RecommendationEventType EventType { get; private set; }

    public int? OriginalPosition { get; private set; }

    public int? NewPosition { get; private set; }

    public bool? WasMandatory { get; private set; }

    public RecommendationCaptureSource Source { get; private set; }

    public DateTimeOffset OccurredAtUtc { get; private set; }

    public Guid ClientEventId { get; private set; }

    public static RecommendationBehaviorEvent Create(
        long travelerUserId,
        long poiId,
        long? itineraryId,
        RecommendationEventType eventType,
        int? originalPosition,
        int? newPosition,
        bool? wasMandatory,
        RecommendationCaptureSource source,
        DateTimeOffset occurredAtUtc,
        Guid clientEventId)
    {
        var hasValidSourceContext = source switch
        {
            RecommendationCaptureSource.Itinerary =>
                itineraryId.HasValue && wasMandatory.HasValue,
            RecommendationCaptureSource.Explore or RecommendationCaptureSource.PoiDetail =>
                itineraryId is null && wasMandatory is null,
            _ => false,
        };
        if (!hasValidSourceContext)
        {
            throw new ArgumentException(
                "The itinerary context must match the recommendation capture source.",
                nameof(source));
        }

        if (eventType is RecommendationEventType.Skip or RecommendationEventType.Reorder
            && source != RecommendationCaptureSource.Itinerary)
        {
            throw new ArgumentException(
                "Skip and reorder events require an itinerary source.",
                nameof(source));
        }

        var hasValidPositions = eventType switch
        {
            RecommendationEventType.Like or RecommendationEventType.Dislike =>
                originalPosition is null && newPosition is null,
            RecommendationEventType.Skip =>
                originalPosition.HasValue && newPosition is null,
            RecommendationEventType.Reorder =>
                originalPosition.HasValue
                && newPosition.HasValue
                && originalPosition != newPosition,
            _ => false,
        };
        if (!hasValidPositions)
        {
            throw new ArgumentException(
                "The positions must match the recommendation event type.",
                nameof(eventType));
        }

        return new RecommendationBehaviorEvent
        {
            TravelerUserId = travelerUserId,
            PointOfInterestId = poiId,
            ItineraryId = itineraryId,
            EventType = eventType,
            OriginalPosition = originalPosition,
            NewPosition = newPosition,
            WasMandatory = wasMandatory,
            Source = source,
            OccurredAtUtc = occurredAtUtc,
            ClientEventId = clientEventId,
        };
    }
}