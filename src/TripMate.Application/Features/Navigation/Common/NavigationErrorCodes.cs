namespace TripMate.Application.Features.Navigation.Common;

public static class NavigationErrorCodes
{
    public const string UnsupportedStateFilter = "navigation.unsupported_state_filter";
    public const string AccessDenied = "navigation.access_denied";
    public const string SessionNotFound = "navigation.session_not_found";
    public const string ItineraryNotActive = "navigation.itinerary_not_active";
    public const string OutsideTripWindow = "navigation.outside_trip_window";
    public const string ActiveSessionExists = "navigation.active_session_exists";
    public const string IdempotencyKeyPayloadMismatch = "navigation.idempotency_key_payload_mismatch";
    public const string ItemAlreadyReached = "navigation.item_already_reached";
    public const string SessionCompleted = "navigation.session_completed";
    public const string NoNavigableItems = "navigation.no_navigable_items";
    public const string ItineraryScheduleIncomplete = "navigation.itinerary_schedule_incomplete";
    public const string ItemNotNavigable = "navigation.item_not_navigable";
}

public static class NavigationErrorMetadata
{
    public const string ActiveSessionId = "activeSessionId";
    public const string ActiveSessionLocation = "activeSessionLocation";
}