namespace TripMate.Application.Features.Scheduling.Common;

public sealed class SchedulingRateLimitOptions
{
    public const string SectionName = "SchedulingRateLimit";

    public int MaxFreshGenerationsPerMinute { get; init; } = 3;

    public int CooldownSeconds { get; init; } = 15;
}