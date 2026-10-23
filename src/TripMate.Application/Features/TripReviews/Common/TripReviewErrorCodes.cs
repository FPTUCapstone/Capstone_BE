namespace TripMate.Application.Features.TripReviews.Common;

public static class TripReviewErrorCodes
{
    public const string Unauthorized = "trip_review.unauthorized";
    public const string Forbidden = "trip_review.forbidden";
    public const string BookingNotFound = "trip_review.booking_not_found";
    public const string InvalidInput = "trip_review.invalid_input";
    public const string InvalidEditPayload = "trip_review.invalid_edit_payload";
    public const string CspIneligible = "trip_review.csp_ineligible";
    public const string RoutePacingUnavailable = "trip_review.route_pacing_unavailable";
    public const string PoiUnavailable = "trip_review.poi_unavailable";
    public const string PoiIneligible = "trip_review.poi_ineligible";
    public const string Duplicate = "trip_review.duplicate";
    public const string StaleVersion = "trip_review.stale_version";
    public const string EditExpired = "trip_review.edit_expired";
    public const string BookingNotCompleted = "trip_review.booking_not_completed";
    public const string InconsistentContext = "trip_review.inconsistent_context";
    public const string UnsupportedSubject = "trip_review.unsupported_subject";
    public const string PolicyRejected = "trip_review.policy_rejected";
    public const string PolicyUnavailable = "trip_review.policy_unavailable";
    public const string LegacyConflict = "trip_review.legacy_conflict";
    public const string StorageUnavailable = "trip_review.storage_unavailable";
    public const string BodyTooLarge = "trip_review.body_too_large";
    public const string UnsupportedMediaType = "trip_review.unsupported_media_type";
}