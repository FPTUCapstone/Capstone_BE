using System.Text.Json.Serialization;

using TripMate.Domain.Enums;

namespace TripMate.Application.Features.RecommendationFeedback.Capture;

public sealed record CaptureRecommendationFeedbackResponse(
    long EventId,
    Guid ClientEventId,
    RecommendationEventType EventType,
    long PoiId,
    long? ItineraryId,
    int? OriginalPosition,
    int? NewPosition,
    bool? WasMandatory,
    RecommendationCaptureSource Source,
    DateTimeOffset OccurredAtUtc,
    [property: JsonIgnore] bool IsReplay);
