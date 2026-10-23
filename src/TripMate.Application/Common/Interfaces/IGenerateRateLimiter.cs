namespace TripMate.Application.Common.Interfaces;

public interface IGenerateRateLimiter
{
    ValueTask<GenerateRateLimitDecision> TryAcquireAsync(
        long userId,
        CancellationToken cancellationToken);
}

public sealed record GenerateRateLimitDecision(
    bool Allowed,
    string? ErrorCode = null,
    int RetryAfterSeconds = 0);