namespace TripMate.Infrastructure.Services;

public enum SchedulingRateLimitProvider
{
    Redis = 0,
    SingleInstance = 1,
}

public sealed class SchedulingRateLimitOptions
{
    public const string SectionName = "SchedulingRateLimit";

    public SchedulingRateLimitProvider Provider { get; init; } = SchedulingRateLimitProvider.Redis;

    public int MaxFreshGenerationsPerMinute { get; init; } = 3;

    public int CooldownSeconds { get; init; } = 15;

    public string RedisConnectionStringName { get; init; } = "Redis";

    public string KeyPrefix { get; init; } = "tripmate:scheduling:rate-limit";

    public TimeSpan CommandTimeout { get; init; } = TimeSpan.FromSeconds(2);

    public TimeSpan TtlSafetyMargin { get; init; } = TimeSpan.FromSeconds(15);

    public TimeSpan IdleTtl { get; init; } = TimeSpan.FromMinutes(5);

    public TimeSpan CleanupCadence { get; init; } = TimeSpan.FromMinutes(1);

    public int MaxTrackedUsers { get; init; } = 10_000;
}