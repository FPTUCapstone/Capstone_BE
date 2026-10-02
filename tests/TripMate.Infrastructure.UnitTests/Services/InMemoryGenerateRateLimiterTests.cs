using FluentAssertions;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Features.Scheduling.Common;
using TripMate.Infrastructure.Services;

namespace TripMate.Infrastructure.UnitTests.Services;

public sealed class InMemoryGenerateRateLimiterTests
{
    private readonly MutableClock _clock = new(new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero));

    [Fact]
    public void EnforcesCooldownAndRollingWindowWithoutBlockingAnotherUser()
    {
        var limiter = Create(max: 3, cooldown: 15);

        limiter.TryAcquire(1).Allowed.Should().BeTrue();
        limiter.TryAcquire(2).Allowed.Should().BeTrue();

        var cooldown = limiter.TryAcquire(1);
        cooldown.Allowed.Should().BeFalse();
        cooldown.ErrorCode.Should().Be(SchedulingErrorCodes.GenerationCooldown);
        cooldown.RetryAfterSeconds.Should().Be(15);

        _clock.Advance(TimeSpan.FromSeconds(15));
        limiter.TryAcquire(1).Allowed.Should().BeTrue();
        _clock.Advance(TimeSpan.FromSeconds(15));
        limiter.TryAcquire(1).Allowed.Should().BeTrue();
        _clock.Advance(TimeSpan.FromSeconds(15));
        limiter.TryAcquire(1).ErrorCode.Should().Be(SchedulingErrorCodes.GenerationRateLimited);

        _clock.Advance(TimeSpan.FromSeconds(16));
        limiter.TryAcquire(1).Allowed.Should().BeTrue();
    }

    [Fact]
    public async Task SimultaneousRequestsOnlyAcquireOnePermitDuringCooldown()
    {
        var limiter = Create(max: 3, cooldown: 15);
        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ =>
            Task.Run(() => limiter.TryAcquire(42))));

        results.Count(result => result.Allowed).Should().Be(1);
    }

    private InMemoryGenerateRateLimiter Create(int max, int cooldown) =>
        new(_clock, new SchedulingRateLimitOptions
        {
            MaxFreshGenerationsPerMinute = max,
            CooldownSeconds = cooldown,
        });

    private sealed class MutableClock(DateTimeOffset value) : IDateTimeProvider
    {
        public DateTimeOffset UtcNow { get; private set; } = value;

        public void Advance(TimeSpan duration) => UtcNow += duration;
    }
}