namespace TripMate.Application.Features.Scheduling.Common;

public static class SchedulingErrorCodes
{
    public const string ConstraintsInfeasible = "planning.constraints_infeasible";
    public const string MandatoryFoodNotAllowed = "planning.mandatory_food_not_allowed";
    public const string IdempotencyKeyPayloadMismatch = "planning.idempotency_key_payload_mismatch";
    public const string GenerationCooldown = "planning.generation_cooldown";
    public const string GenerationRateLimited = "planning.generation_rate_limited";
    public const string GenerationRateLimiterUnavailable =
        "planning.generation_rate_limiter_unavailable";
    public const string GenerationTemporarilyUnavailable =
        "planning.generation_temporarily_unavailable";
    public const string RoutingProviderUnavailable = "planning.routing_provider_unavailable";

    /// <summary>
    /// The CSP reached its time limit and the heuristic found no plan. The timed-out CSP result is discarded, so this does
    /// not prove the request infeasible: it is retryable (ADR-002, G2).
    /// </summary>
    public const string GenerationTimedOut = "planning.generation_timeout";
}