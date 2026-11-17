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

    /// <summary>
    /// Background mode (specs/UC-10-background-explanation-spec.md): the create request answers with the template
    /// sentences and the AI explanation is written afterwards. Has no effect while <see cref="Enabled"/> is false.
    /// </summary>
    public bool BackgroundEnabled { get; init; } = false;

    /// <summary>
    /// Seconds one background explanation may take (1-300). Separate from <see cref="OverallTimeoutSeconds"/>, whose
    /// 30-second ceiling protects the request the traveler waits on. Keep it longer than the AI service's
    /// LLM_TIMEOUT_SECONDS, so the backend is still listening when a slow answer arrives.
    /// </summary>
    public int BackgroundTimeoutSeconds { get; init; } = 100;
}