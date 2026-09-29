namespace TripMate.Application.Features.RecommendationFeedback.Common;

public static class FeedbackErrorCodes
{
    public const string PoiNotFound = "feedback.poi_not_found";
    public const string ItineraryNotFound = "feedback.itinerary_not_found";
    public const string ContextMismatch = "feedback.context_mismatch";
    public const string EventTokenConflict = "feedback.event_token_conflict";
}
