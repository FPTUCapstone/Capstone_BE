using System.Diagnostics;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Features.Scheduling.Common;

namespace TripMate.Infrastructure.Services;

public sealed class InMemoryGenerateRateLimiter : IGenerateRateLimiter
{
    private readonly object _sync = new();
    private readonly Dictionary<long, UserWindow> _users = [];
    private readonly IDateTimeProvider _clock;
    private readonly SchedulingRateLimitOptions _options;
    private readonly GenerateRateLimiterMetrics? _metrics;
    private DateTimeOffset _lastCleanup;

    public InMemoryGenerateRateLimiter(
        IDateTimeProvider clock,
        SchedulingRateLimitOptions options,
        GenerateRateLimiterMetrics? metrics = null)
    {
        _clock = clock;
        _options = options;
        _metrics = metrics;
        _lastCleanup = clock.UtcNow;
        _metrics?.RegisterActiveUsersCallback("single_instance", () => TrackedUserCount);
    }

    internal int TrackedUserCount
    {
        get
        {
            lock (_sync)
            {
                return _users.Count;
            }
        }
    }

    public ValueTask<GenerateRateLimitDecision> TryAcquireAsync(
        long userId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return new ValueTask<GenerateRateLimitDecision>(TryAcquireCore(userId));
    }

    private GenerateRateLimitDecision TryAcquireCore(long userId)
    {
        var stopwatch = Stopwatch.StartNew();

        lock (_sync)
        {
            DateTimeOffset now = _clock.UtcNow;

            if (now - _lastCleanup >= _options.CleanupCadence)
            {
                SweepExpiredEntries(now);
                _lastCleanup = now;
            }

            if (!_users.TryGetValue(userId, out UserWindow? state))
            {
                if (_users.Count >= _options.MaxTrackedUsers)
                {
                    SweepExpiredEntries(now);
                    _lastCleanup = now;

                    if (_users.Count >= _options.MaxTrackedUsers)
                    {
                        stopwatch.Stop();
                        _metrics?.RecordAcquisition("single_instance", "store_failure");
                        _metrics?.RecordDuration("single_instance", "failure", stopwatch.Elapsed.TotalMilliseconds);

                        return new GenerateRateLimitDecision(
                            Allowed: false,
                            ErrorCode: SchedulingErrorCodes.GenerationRateLimiterUnavailable);
                    }
                }

                state = new UserWindow(now);
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
                    stopwatch.Stop();
                    _metrics?.RecordAcquisition("single_instance", "cooldown");
                    _metrics?.RecordDuration("single_instance", "success", stopwatch.Elapsed.TotalMilliseconds);

                    return new GenerateRateLimitDecision(
                        Allowed: false,
                        ErrorCode: SchedulingErrorCodes.GenerationCooldown,
                        RetryAfterSeconds: RetryAfter(remaining));
                }
            }

            if (state.Accepted.Count >= _options.MaxFreshGenerationsPerMinute)
            {
                stopwatch.Stop();
                _metrics?.RecordAcquisition("single_instance", "quota");
                _metrics?.RecordDuration("single_instance", "success", stopwatch.Elapsed.TotalMilliseconds);

                return new GenerateRateLimitDecision(
                    Allowed: false,
                    ErrorCode: SchedulingErrorCodes.GenerationRateLimited,
                    RetryAfterSeconds: RetryAfter(state.Accepted.Peek().AddMinutes(1) - now));
            }

            state.Accepted.Enqueue(now);
            state.LastAccepted = now;
            state.LastActivityAt = now;

            stopwatch.Stop();
            _metrics?.RecordAcquisition("single_instance", "accepted");
            _metrics?.RecordDuration("single_instance", "success", stopwatch.Elapsed.TotalMilliseconds);

            return new GenerateRateLimitDecision(Allowed: true);
        }
    }

    internal int SweepExpiredEntries(DateTimeOffset now)
    {
        var expiredKeys = new List<long>();
        foreach (var (userId, state) in _users)
        {
            if (now - state.LastActivityAt >= _options.IdleTtl)
            {
                expiredKeys.Add(userId);
            }
        }

        foreach (var key in expiredKeys)
        {
            _users.Remove(key);
        }

        if (expiredKeys.Count > 0)
        {
            _metrics?.RecordEvictions("single_instance", expiredKeys.Count);
        }

        return expiredKeys.Count;
    }

    private static int RetryAfter(TimeSpan remaining) =>
        Math.Clamp((int)Math.Ceiling(remaining.TotalSeconds), 1, 60);

    private sealed class UserWindow(DateTimeOffset createdAt)
    {
        public Queue<DateTimeOffset> Accepted { get; } = new();
        public DateTimeOffset? LastAccepted { get; set; }
        public DateTimeOffset LastActivityAt { get; set; } = createdAt;
    }
}