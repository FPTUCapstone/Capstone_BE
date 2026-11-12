using TripMate.Application.Features.Bookings.Cancel;

namespace TripMate.Application.UnitTests.Features.Bookings.Cancel;

// BR-135 (owner decision 11/11/2026): more than 14 days before departure 100%; 10 to 14 days 50%;
// less than 10 days 0% (MSG170 confirmation required). Replaces the v2 14/7-day, 72-hour tiers.
// Applies only to bookings created on or after the cutover date (Q1) — v1/v2 snapshots on
// existing bookings keep reading as before (BR-107), covered by the sibling V1/V2 test files.
public sealed class TravelerCancellationPolicyV3Tests
{
    private const string V3Snapshot =
        "{\"version\":3,\"atLeastSevenDaysPercent\":100,\"threeToSevenDaysPercent\":50,\"oneToThreeDaysPercent\":0}";

    private static readonly DateTimeOffset Departure = new(2026, 11, 15, 8, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(-(14 * 24 * 60 + 1), 100)] // more than 14 days
    [InlineData(-(14 * 24 * 60), 50)] // exactly 14 days
    [InlineData(-(10 * 24 * 60), 50)] // exactly 10 days
    [InlineData(-(10 * 24 * 60 - 1), 0)] // just under 10 days
    [InlineData(0, 0)] // at departure
    public void RefundPercent_V3FollowsBr135Boundaries(int minutesBeforeDeparture, decimal expectedPercent)
    {
        Assert.True(TravelerCancellationPolicy.TryParse(V3Snapshot, out var policy));

        decimal? actual = policy!.RefundPercent(Departure.AddMinutes(minutesBeforeDeparture), Departure);

        Assert.Equal(expectedPercent, actual);
    }

    [Fact]
    public void RefundPercent_V3NeverBlocksCancellation()
    {
        // Unlike v1 (null = blocked under 24h), v3 always returns a percent — 0% within 10 days,
        // shown with a warning and confirmed separately (MSG170), never a hard block.
        Assert.True(TravelerCancellationPolicy.TryParse(V3Snapshot, out var policy));

        decimal? actual = policy!.RefundPercent(Departure.AddMinutes(-1), Departure);

        Assert.Equal(0m, actual);
        Assert.NotNull(actual);
    }

    [Fact]
    public void TryParse_AcceptsVersionThree()
    {
        Assert.True(TravelerCancellationPolicy.TryParse(V3Snapshot, out var policy));

        Assert.Equal(3, policy!.Version);
    }

    [Fact]
    public void DefaultSnapshot_IsVersionThree()
    {
        // Booking.DefaultCancellationPolicySnapshot moves to v3 for bookings created from the
        // cutover date onward (Q1) — existing bookings keep their own stored v1/v2 snapshot.
        Assert.True(TravelerCancellationPolicy.TryParse(TravelerCancellationPolicy.DefaultSnapshot, out var policy));

        Assert.Equal(3, policy!.Version);
    }
}
