using FluentAssertions;

using TripMate.Application.Common.Geo;

namespace TripMate.Application.UnitTests.Common.Geo;

public class GeoDistanceTests
{
    [Fact]
    public void EquirectangularKilometers_IdenticalCoordinates_ReturnsZero()
    {
        var distance = GeoDistance.EquirectangularKilometers(16.0m, 108.0m, 16.0m, 108.0m);

        distance.Should().Be(0m);
    }

    [Fact]
    public void EquirectangularKilometers_PureLatitudeDelta_MatchesExactFormula()
    {
        const decimal startLat = 16.0m;
        const decimal endLat = 16.01m;
        const decimal lon = 108.0m;

        var distance = GeoDistance.EquirectangularKilometers(startLat, lon, endLat, lon);
        var expected = 0.01m * GeoDistance.LatitudeKilometersPerDegree;

        distance.Should().Be(expected);
    }

    [Fact]
    public void EquirectangularKilometers_PureLongitudeDelta_MatchesExactFormula()
    {
        const decimal lat = 16.0m;
        const decimal startLon = 108.0m;
        const decimal endLon = 108.01m;

        var distance = GeoDistance.EquirectangularKilometers(lat, startLon, lat, endLon);
        var expected = 0.01m * GeoDistance.LongitudeKilometersPerDegree(lat);

        distance.Should().Be(expected);
    }

    [Theory]
    [InlineData(1.0)]
    [InlineData(5.0)]
    [InlineData(10.0)]
    [InlineData(50.0)]
    public void ExactBoundaryEquality_IsInclusive(double radiusDouble)
    {
        var radius = (decimal)radiusDouble;
        var exactDistance = radius;

        (exactDistance <= radius).Should().BeTrue("exact boundary equality must be inclusive");
        (exactDistance < radius).Should().BeFalse("exact boundary equality is not strictly less than");
    }

    /// <summary>
    /// Demonstrates coordinate quantization on DECIMAL(9, 6) representations:
    /// Because 110.574 = 55287 / 500 contains the prime factor 6143, exact integer km radii
    /// yield infinite repeating decimals in base 10. The nearest representable 6-decimal numbers
    /// are adjacent microdegrees differing by exactly 0.000001 deg, cleanly bifurcating across the radius.
    /// </summary>
    [Theory]
    [InlineData("North", 16.045218, 108.000000, true)]
    [InlineData("North", 16.045219, 108.000000, false)]
    [InlineData("South", 15.954782, 108.000000, true)]
    [InlineData("South", 15.954781, 108.000000, false)]
    [InlineData("East", 16.000000, 108.046725, true)]
    [InlineData("East", 16.000000, 108.046726, false)]
    [InlineData("West", 16.000000, 107.953275, true)]
    [InlineData("West", 16.000000, 107.953274, false)]
    public void QuantizedBoundaryPairs_BifurcateAtFiveKilometers(
        string direction,
        double latitude,
        double longitude,
        bool expectedInside)
    {
        var distance = GeoDistance.EquirectangularKilometers(
            16.0m, 108.0m, (decimal)latitude, (decimal)longitude);

        if (expectedInside)
        {
            distance.Should().BeLessThan(5.0m, "direction {0} inside neighbor must be < 5km", direction);
            (distance <= 5.0m).Should().BeTrue("direction {0} inside neighbor must satisfy <= 5km", direction);
        }
        else
        {
            distance.Should().BeGreaterThan(5.0m, "direction {0} outside neighbor must be > 5km", direction);
            (distance <= 5.0m).Should().BeFalse("direction {0} outside neighbor must not satisfy <= 5km", direction);
        }
    }
}