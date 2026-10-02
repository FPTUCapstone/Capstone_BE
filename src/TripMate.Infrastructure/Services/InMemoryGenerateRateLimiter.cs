using TripMate.Application.Common.Interfaces;
using TripMate.Application.Features.Scheduling.Common;

namespace TripMate.Infrastructure.Services;

public sealed class InMemoryGenerateRateLimiter(
    IDateTimeProvider clock,
    SchedulingRateLimitOptions options) : IGenerateRateLimiter
{
    private readonly object _sync = new();
    private readonly Dictionary<long, UserWindow> _users = [];
    private readonly IDateTimeProvider _clock = clock;
    private readonly SchedulingRateLimitOptions _options = options;

    public GenerateRateLimitDecision TryAcquire(long userId)
    {
        lock (_sync)
        {
            DateTimeOffset now = _clock.UtcNow;
            if (!_users.TryGetValue(userId, out UserWindow? state))
            {
                state = new UserWindow();
                _users[userId] = state;
            }

            while (state.Accepted.Count > 0
                && now - state.Accepted.Peek() >= TimeSpan.FromMinutes(1))
            {
                state.Accepted.Dequeue();
            }

            if (state.LastAccepted is DateTimeOffset last)
            {
                TimeSpan remaining = last.AddSeconds(_options.CooldownSeconds) - now;
                if (remaining > TimeSpan.Zero)
                {
                    return new GenerateRateLimitDecision(
                        false,
                        SchedulingErrorCodes.GenerationCooldown,
                        RetryAfter(remaining));
                }
            }

            if (state.Accepted.Count >= _options.MaxFreshGenerationsPerMinute)
            {
                return new GenerateRateLimitDecision(
                    false,
                    SchedulingErrorCodes.GenerationRateLimited,
                    RetryAfter(state.Accepted.Peek().AddMinutes(1) - now));
            }

            state.Accepted.Enqueue(now);
            state.LastAccepted = now;
            return new GenerateRateLimitDecision(true);
        }
    }

    private static int RetryAfter(TimeSpan remaining) =>
        Math.Clamp((int)Math.Ceiling(remaining.TotalSeconds), 1, 60);

    private sealed class UserWindow
    {
        public Queue<DateTimeOffset> Accepted { get; } = new();
        public DateTimeOffset? LastAccepted { get; set; }
    }
}
