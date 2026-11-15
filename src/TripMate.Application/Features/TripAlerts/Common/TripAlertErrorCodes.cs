namespace TripMate.Application.Features.TripAlerts.Common;

public static class TripAlertErrorCodes
{
    public const string AccessDenied = "trip_alert.access_denied";
    public const string NotFound = "trip_alert.not_found";
    public const string SessionCompleted = "trip_alert.session_completed";
    public const string TargetNotNavigable = "trip_alert.target_not_navigable";
    public const string TargetAlreadyReached = "trip_alert.target_already_reached";
    public const string EventPayloadMismatch = "trip_alert.event_payload_mismatch";
    public const string DeviationCooldown = "trip_alert.deviation_cooldown";
}