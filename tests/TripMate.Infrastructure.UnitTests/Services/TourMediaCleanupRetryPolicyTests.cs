using FluentAssertions;

using TripMate.Infrastructure.Services;

namespace TripMate.Infrastructure.UnitTests.Services;

public sealed class TourMediaCleanupRetryPolicyTests
{
    [Theory]
    [InlineData(1, 1)]
    [InlineData(2, 2)]
    [InlineData(3, 4)]
    [InlineData(4, 8)]
    [InlineData(5, 16)]
    [InlineData(6, 32)]
    [InlineData(7, 64)]
    [InlineData(8, 128)]
    [InlineData(12, 1440)]
    [InlineData(int.MaxValue, 1440)]
    public void GetDelayForAttempt_UsesExponentialBackoffWithTwentyFourHourCap(
        int attemptCount,
        int expectedMinutes)
    {
        TourMediaCleanupRetryPolicy.GetDelayForAttempt(attemptCount)
            .Should().Be(TimeSpan.FromMinutes(expectedMinutes));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void GetDelayForAttempt_RejectsInvalidAttempt(int attemptCount)
    {
        var action = () => TourMediaCleanupRetryPolicy.GetDelayForAttempt(attemptCount);

        action.Should().Throw<ArgumentOutOfRangeException>();
    }
}