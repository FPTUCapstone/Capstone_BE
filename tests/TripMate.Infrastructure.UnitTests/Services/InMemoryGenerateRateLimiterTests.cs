using FluentAssertions;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Features.Scheduling.Common;
using TripMate.Infrastructure.Services;

namespace TripMate.Infrastructure.UnitTests.Services;

public sealed class InMemoryGenerateRateLimiterTests
{
    private readonly MutableClock _clock = new(new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero));

    [Fact]
    public async Task EnforcesCooldownAndRollingWindowWithoutBlockingAnotherUser()
    {
        var limiter = Create(max: 3, cooldown: 15);

        (await limiter.TryAcquireAsync(1, CancellationToken.None)).Allowed.Should().BeTrue();
        (await limiter.TryAcquireAsync(2, CancellationToken.None)).Allowed.Should().BeTrue();

        var cooldown = await limiter.TryAcquireAsync(1, CancellationToken.None);
        cooldown.Allowed.Should().BeFalse();
        cooldown.ErrorCode.Should().Be(SchedulingErrorCodes.GenerationCooldown);
        cooldown.RetryAfterSeconds.Should().Be(15);

        _clock.Advance(TimeSpan.FromSeconds(15));
        (await limiter.TryAcquireAsync(1, CancellationToken.None)).Allowed.Should().BeTrue();
        _clock.Advance(TimeSpan.FromSeconds(15));
        (await limiter.TryAcquireAsync(1, CancellationToken.None)).Allowed.Should().BeTrue();
        _clock.Advance(TimeSpan.FromSeconds(15));
        (await limiter.TryAcquireAsync(1, CancellationToken.None)).ErrorCode.Should().Be(
            SchedulingErrorCodes.GenerationRateLimited);

        _clock.Advance(TimeSpan.FromSeconds(16));
        (await limiter.TryAcquireAsync(1, CancellationToken.None)).Allowed.Should().BeTrue();
    }

    [Fact]
    public async Task SimultaneousRequestsOnlyAcquireOnePermitDuringCooldown()
    {
        var limiter = Create(max: 3, cooldown: 15);
        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ =>
            Task.Run(async () => await limiter.TryAcquireAsync(42, CancellationToken.None))));

        results.Count(result => result.Allowed).Should().Be(1);
    }

    [Fact]
    public async Task IdleEntries_AreSweptWithoutSameUserCallingAgain()
    {
        var limiter = Create(max: 3, cooldown: 15, idleTtl: TimeSpan.FromMinutes(2), cleanupCadence: TimeSpan.FromMinutes(1));

        (await limiter.TryAcquireAsync(101, CancellationToken.None)).Allowed.Should().BeTrue();
        (await limiter.TryAcquireAsync(102, CancellationToken.None)).Allowed.Should().BeTrue();
        limiter.TrackedUserCount.Should().Be(2);

        // Advance beyond idle TTL and cleanup cadence
        _clock.Advance(TimeSpan.FromMinutes(3));

        // User 103 acquires, triggering periodic sweep
        (await limiter.TryAcquireAsync(103, CancellationToken.None)).Allowed.Should().BeTrue();

        // User 101 and 102 should have been swept, only 103 remains
        limiter.TrackedUserCount.Should().Be(1);
    }

    [Fact]
    public async Task AtCapacity_NewUserFailsClosedWhenNoExpiredEntries()
    {
        var limiter = Create(
            max: 3,
            cooldown: 15,
            idleTtl: TimeSpan.FromMinutes(5),
            cleanupCadence: TimeSpan.FromMinutes(1),
            maxTrackedUsers: 2);

        (await limiter.TryAcquireAsync(1, CancellationToken.None)).Allowed.Should().BeTrue();
        (await limiter.TryAcquireAsync(2, CancellationToken.None)).Allowed.Should().BeTrue();
        limiter.TrackedUserCount.Should().Be(2);

        // Both users are active (within 5 minutes idle TTL). Third user tries to acquire:
        var decision = await limiter.TryAcquireAsync(3, CancellationToken.None);
        decision.Allowed.Should().BeFalse();
        decision.ErrorCode.Should().Be(SchedulingErrorCodes.GenerationRateLimiterUnavailable);
        limiter.TrackedUserCount.Should().Be(2);

        // Now advance beyond idle TTL
        _clock.Advance(TimeSpan.FromMinutes(6));

        // Third user tries to acquire again: sweep clears user 1 and 2, allows user 3
        var retryDecision = await limiter.TryAcquireAsync(3, CancellationToken.None);
        retryDecision.Allowed.Should().BeTrue();
        limiter.TrackedUserCount.Should().Be(1);
    }

    [Fact]
    public async Task RejectedRequests_DoNotExtendUserLifetime()
    {
        var limiter = Create(
            max: 3,
            cooldown: 15,
            idleTtl: TimeSpan.FromMinutes(2),
            cleanupCadence: TimeSpan.FromMinutes(1));

        (await limiter.TryAcquireAsync(1, CancellationToken.None)).Allowed.Should().BeTrue();

        // User 1 makes repeated rejected calls during cooldown
        _clock.Advance(TimeSpan.FromSeconds(5));
        (await limiter.TryAcquireAsync(1, CancellationToken.None)).Allowed.Should().BeFalse();
        _clock.Advance(TimeSpan.FromSeconds(5));
        (await limiter.TryAcquireAsync(1, CancellationToken.None)).Allowed.Should().BeFalse();

        // Advance to 2 minutes from initial acceptance
        _clock.Advance(TimeSpan.FromSeconds(115)); // total elapsed = 125 seconds > 2 minutes

        // User 2 acquires, triggering sweep
        (await limiter.TryAcquireAsync(2, CancellationToken.None)).Allowed.Should().BeTrue();

        // User 1 should have been swept because rejections did NOT extend lifetime
        limiter.TrackedUserCount.Should().Be(1);
    }

    [Fact]
    public async Task ConcurrentCalls_CannotOverGrantQuota()
    {
        var limiter = Create(max: 3, cooldown: 0); // 3 per minute, no cooldown
        var results = await Task.WhenAll(Enumerable.Range(0, 16).Select(_ =>
            Task.Run(async () => await limiter.TryAcquireAsync(99, CancellationToken.None))));

        results.Count(result => result.Allowed).Should().Be(3);
        results.Count(result => !result.Allowed && result.ErrorCode == SchedulingErrorCodes.GenerationRateLimited).Should().Be(13);
    }

    private InMemoryGenerateRateLimiter Create(
        int max,
        int cooldown,
        TimeSpan? idleTtl = null,
        TimeSpan? cleanupCadence = null,
        int maxTrackedUsers = 10_000) =>
        new(_clock, new SchedulingRateLimitOptions
        {
            Provider = SchedulingRateLimitProvider.SingleInstance,
            MaxFreshGenerationsPerMinute = max,
            CooldownSeconds = cooldown,
            IdleTtl = idleTtl ?? TimeSpan.FromMinutes(5),
            CleanupCadence = cleanupCadence ?? TimeSpan.FromMinutes(1),
            MaxTrackedUsers = maxTrackedUsers,
        });

    private sealed class MutableClock(DateTimeOffset value) : IDateTimeProvider
    {
        public DateTimeOffset UtcNow { get; private set; } = value;

        public void Advance(TimeSpan duration) => UtcNow += duration;
    }
}