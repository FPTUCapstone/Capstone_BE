using TripMate.Domain.Enums;

namespace TripMate.Api.Controllers.V1.Requests;

public sealed record CaptureRecommendationFeedbackRequest(
    Guid ClientEventId,
    RecommendationEventType EventType,
    long PoiId,
    long? ItineraryId,
    int? OriginalPosition,
    int? NewPosition,
    RecommendationCaptureSource Source);