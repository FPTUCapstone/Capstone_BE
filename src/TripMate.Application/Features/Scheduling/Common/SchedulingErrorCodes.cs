namespace TripMate.Application.Features.Scheduling.Common;

public static class SchedulingErrorCodes
{
    public const string ConstraintsInfeasible = "planning.constraints_infeasible";
    public const string IdempotencyKeyPayloadMismatch = "planning.idempotency_key_payload_mismatch";
    public const string GenerationCooldown = "planning.generation_cooldown";
    public const string GenerationRateLimited = "planning.generation_rate_limited";
    public const string RoutingProviderUnavailable = "planning.routing_provider_unavailable";
}