namespace TripMate.Application.Features.Operator.Tours.Emergency;

public static class EmergencyTourErrorCodes
{
    public const string NotFound = "tour_schedule.not_found";
    public const string Forbidden = "tour_schedule.cancellation_forbidden";
    public const string AlreadyTerminal = "tour_schedule.already_terminal";
    public const string AlreadyCompleted = "tour_schedule.already_completed";
    public const string InvalidRequest = "tour_schedule.emergency_reason_required";
    public const string ConcurrentStateChanged = "tour_schedule.concurrent_state_changed";
    public const string WrongActionWindow = "tour_schedule.emergency_action_window_invalid";
    public const string IdempotencyPayloadMismatch = "tour_schedule.idempotency_payload_mismatch";
    public const string InvalidPassword = "user.password_invalid";
    public const string TripSessionLinkUnverified = "trip_session.link_unverified";
    public const string EvidenceNotPending = "tour_schedule.emergency_evidence_not_pending";
    public const string EvidenceStale = "tour_schedule.emergency_evidence_stale";
}
