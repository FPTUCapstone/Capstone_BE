namespace TripMate.Application.Features.Scheduling.Common;

public static class SchedulingErrorCodes
{
    public const string InvalidRequest = "planning.invalid_request";
    public const string ConstraintsInfeasible = "planning.constraints_infeasible";
    public const string DailyGenerationLimitReached = "planning.daily_generation_limit_reached";
    public const string IdempotencyKeyPayloadMismatch = "planning.idempotency_key_payload_mismatch";
}
