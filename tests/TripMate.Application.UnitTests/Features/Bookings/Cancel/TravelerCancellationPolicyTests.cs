using TripMate.Application.Features.Bookings.Cancel;

namespace TripMate.Application.UnitTests.Features.Bookings.Cancel;

public sealed class TravelerCancellationPolicyTests
{
    private const string LegacySnapshot =
        "{\"atLeastSevenDaysPercent\":100,\"threeToSevenDaysPercent\":70,\"oneToThreeDaysPercent\":30}";

    private static readonly DateTimeOffset Departure = new(2026, 11, 15, 8, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(-168, 100)]
    [InlineData(-72, 70)]
    [InlineData(-24, 30)]
    public void RefundPercent_UsesInclusivePolicyBoundaries(int hoursBeforeDeparture, decimal expectedPercent)
    {
        Assert.True(TravelerCancellationPolicy.TryParse(LegacySnapshot, out var policy));

        decimal? actual = policy!.RefundPercent(Departure.AddHours(hoursBeforeDeparture), Departure);

        Assert.Equal(expectedPercent, actual);
    }

    [Fact]
    public void RefundPercent_RejectsTravelerCancellationInsideTwentyFourHours()
    {
        Assert.True(TravelerCancellationPolicy.TryParse(LegacySnapshot, out var policy));

        decimal? actual = policy!.RefundPercent(Departure.AddHours(-23.99), Departure);

        Assert.Null(actual);
    }

    [Fact]
    public void TryParse_RejectsMalformedSnapshot()
    {
        Assert.False(TravelerCancellationPolicy.TryParse("{not-json}", out var policy));
        Assert.Null(policy);
    }
}