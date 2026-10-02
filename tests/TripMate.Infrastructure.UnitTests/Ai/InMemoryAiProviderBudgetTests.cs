using FluentAssertions;

using TripMate.Application.Common.Interfaces;
using TripMate.Infrastructure.Ai;

namespace TripMate.Infrastructure.UnitTests.Ai;

public sealed class InMemoryAiProviderBudgetTests
{
    [Fact]
    public void RateLimitConsumesPermitsAndRestoresAfterTheRollingWindow()
    {
        var clock = new MutableClock(new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero));
        var budget = Create(clock, rate: 2, concurrency: 2);

        budget.TryAcquire(AiProviderNames.Explanation, out IDisposable? first).Should().BeTrue();
        first!.Dispose();
        budget.TryAcquire(AiProviderNames.Explanation, out IDisposable? second).Should().BeTrue();
        second!.Dispose();
        budget.TryAcquire(AiProviderNames.Explanation, out _).Should().BeFalse();

        clock.Advance(TimeSpan.FromMinutes(1));
        budget.TryAcquire(AiProviderNames.Explanation, out IDisposable? restored).Should().BeTrue();
        restored!.Dispose();
    }

    [Fact]
    public async Task ConcurrencyGateDoesNotQueueAThirdCall()
    {
        var clock = new MutableClock(DateTimeOffset.UtcNow);
        var budget = Create(clock, rate: 10, concurrency: 2);
        budget.TryAcquire(AiProviderNames.Ranking, out IDisposable? first).Should().BeTrue();
        budget.TryAcquire(AiProviderNames.Ranking, out IDisposable? second).Should().BeTrue();
        budget.TryAcquire(AiProviderNames.Ranking, out _).Should().BeFalse();

        first!.Dispose();
        budget.TryAcquire(AiProviderNames.Ranking, out IDisposable? third).Should().BeTrue();
        second!.Dispose();
        third!.Dispose();
        await Task.CompletedTask;
    }

    private static InMemoryAiProviderBudget Create(
        IDateTimeProvider clock,
        int rate,
        int concurrency) =>
        new(clock,
            new AiProviderBudgetSettings(rate, concurrency),
            new AiProviderBudgetSettings(rate, concurrency));

    private sealed class MutableClock(DateTimeOffset value) : IDateTimeProvider
    {
        public DateTimeOffset UtcNow { get; private set; } = value;

        public void Advance(TimeSpan duration) => UtcNow += duration;
    }
}