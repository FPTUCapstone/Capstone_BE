namespace TripMate.Infrastructure.AiExplanation;

public sealed class ExplanationProviderOptions
{
    public const string SectionName = "AiExplanation";

    public bool Enabled { get; init; } = false;

    public string? Endpoint { get; init; }

    public string? ApiKey { get; init; }

    public string? ModelName { get; init; }

    public int TimeoutSeconds { get; init; } = 10;

    public int OverallTimeoutSeconds { get; init; } = 20;

    public int MaxAttempts { get; init; } = 2;

    public int RetryBaseDelayMilliseconds { get; init; } = 500;

    public int RateLimitPerMinute { get; init; } = 4;

    public int MaxConcurrency { get; init; } = 2;
}
