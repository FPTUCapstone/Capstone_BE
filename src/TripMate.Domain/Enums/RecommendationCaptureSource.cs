namespace TripMate.Domain.Enums;

public enum RecommendationCaptureSource
{
    Itinerary,
    Explore,
    PoiDetail,

    // UC-10 item 6: signals the backend derives from what the traveler does, not from a feedback button.
    /// <summary>A stop removed from (Dislike) or added to (Like) an itinerary when the traveler adjusts it.</summary>
    ItineraryEdit,

    /// <summary>A stop reached (Like) or skipped (Skip) during live navigation.</summary>
    Navigation,

    /// <summary>A POI the traveler marked mandatory when creating a scheduling request (Like).</summary>
    SchedulingRequest,
}