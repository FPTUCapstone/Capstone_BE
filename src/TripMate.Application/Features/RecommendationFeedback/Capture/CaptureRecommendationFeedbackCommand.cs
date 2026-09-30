using MediatR;

using TripMate.Application.Common.Models;
using TripMate.Domain.Enums;

namespace TripMate.Application.Features.RecommendationFeedback.Capture;

public sealed record CaptureRecommendationFeedbackCommand(
    long TravelerUserId,
    Guid ClientEventId,
    RecommendationEventType EventType,
    long PoiId,
    long? ItineraryId,
    int? OriginalPosition,
    int? NewPosition,
    RecommendationCaptureSource Source)
    : IRequest<Result<CaptureRecommendationFeedbackResponse>>;