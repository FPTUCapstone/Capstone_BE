using TripMate.Application.Features.Bookings.Cancel;

namespace TripMate.Application.UnitTests.Features.Bookings.Cancel;

public sealed class TravelerCancellationPolicyV2Tests
{
    private const string V2Snapshot =
        "{\"version\":2,\"atLeastSevenDaysPercent\":100,\"threeToSevenDaysPercent\":50,\"oneToThreeDaysPercent\":30}";

    private static readonly DateTimeOffset Departure = new(2026, 11, 15, 8, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(-(14 * 24 * 60 + 1), 100)]
    [InlineData(-(14 * 24 * 60), 50)]
    [InlineData(-(7 * 24 * 60), 50)]
    [InlineData(-(7 * 24 * 60 - 1), 30)]
    [InlineData(-(72 * 60), 30)]
    [InlineData(-(72 * 60 - 1), 0)]
    [InlineData(0, 0)]
    public void RefundPercent_V2FollowsBr135Boundaries(int minutesBeforeDeparture, decimal expectedPercent)
    {
        Assert.True(TravelerCancellationPolicy.TryParse(V2Snapshot, out var policy));

        decimal? actual = policy!.RefundPercent(Departure.AddMinutes(minutesBeforeDeparture), Departure);

        Assert.Equal(expectedPercent, actual);
    }

    [Fact]
    public void RefundPercent_V2InsideSeventyTwoHoursReturnsZeroInsteadOfBlocking()
    {
        Assert.True(TravelerCancellationPolicy.TryParse(V2Snapshot, out var policy));

        decimal? actual = policy!.RefundPercent(Departure.AddHours(-23.99), Departure);

        Assert.Equal(0m, actual);
    }

    [Fact]
    public void TryParse_SnapshotWithoutVersionIsVersionOne()
    {
        const string legacy = "{\"atLeastSevenDaysPercent\":100,\"threeToSevenDaysPercent\":70,\"oneToThreeDaysPercent\":30}";

        Assert.True(TravelerCancellationPolicy.TryParse(legacy, out var policy));

        Assert.Equal(1, policy!.Version);
        Assert.Null(policy.RefundPercent(Departure.AddHours(-23.99), Departure));
    }

    [Fact]
    public void DefaultSnapshot_IsVersionTwo()
    {
        Assert.True(TravelerCancellationPolicy.TryParse(TravelerCancellationPolicy.DefaultSnapshot, out var policy));

        Assert.Equal(2, policy!.Version);
    }
}