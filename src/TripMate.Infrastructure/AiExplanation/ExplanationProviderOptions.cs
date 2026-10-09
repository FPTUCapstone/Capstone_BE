using TripMate.Infrastructure.Ai;

namespace TripMate.Infrastructure.AiExplanation;

public sealed class ExplanationProviderOptions
{
    public const string SectionName = "AiExplanation";

    public bool Enabled { get; init; } = false;

    /// <summary>
    /// Gemini (mặc định) hoặc TripMateAi. Với TripMateAi, Endpoint là URL đầy đủ của service nội bộ
    /// (ví dụ http://ai:8000/v1/rank-pois), ApiKey là khóa dùng chung, ModelName không bắt buộc.
    /// </summary>
    public AiProviderKind Provider { get; init; } = AiProviderKind.Gemini;

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