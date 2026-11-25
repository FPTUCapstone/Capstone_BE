namespace TripMate.Application.Features.Operator.Tours.Common;

public static class OperatorTourErrorCodes
{
    public const string AccessRequired = "operator_tour.access_required";
    public const string PoiNotFound = "operator_tour.poi_not_found";
    public const string CategoryNotFound = "operator_tour.category_not_found";
    public const string DestinationNotFound = "operator_tour.destination_not_found";
    public const string IdempotencyKeyRequired = "operator_tour.idempotency_key_required";
    public const string IdempotencyKeyPayloadMismatch = "operator_tour.idempotency_key_payload_mismatch";
    public const string TourNotFound = "operator_tour.not_found";
    public const string InvalidState = "operator_tour.invalid_state";
    public const string ReviewPrerequisitesMissing = "operator_tour.review_prerequisites_missing";
    public const string EditLocked = "operator_tour.edit_locked";
    public const string CancellationPolicyUnavailable = "operator_tour.cancellation_policy_unavailable";
    public const string AdminAccessRequired = "operator_tour.admin_access_required";
    public const string RejectionReasonInvalid = "operator_tour.rejection_reason_invalid";
    public const string ReviewChecklistIncomplete = "operator_tour.review_checklist_incomplete";
    public const string ServiceUnavailable = "MSG127";
    public const string WeatherReviewInvalid = "operator_tour.weather_review_invalid";

    // TM-82 / UC-36: draft versions of an Approved/Published tour.
    public const string PendingChangeNotFound = "operator_tour.pending_change_not_found";
    public const string ScheduleHasActiveBookings = "operator_tour.schedule_has_active_bookings";
    public const string ScheduleNotOwnedByTour = "operator_tour.schedule_not_owned_by_tour";
    public const string InvalidScheduleDate = "operator_tour.invalid_schedule_date";
}