namespace TripMate.Application.Common.Interfaces;

public interface IGenerateRateLimiter
{
    GenerateRateLimitDecision TryAcquire(long userId);
}

public sealed record GenerateRateLimitDecision(
    bool Allowed,
    string? ErrorCode = null,
    int RetryAfterSeconds = 0);
