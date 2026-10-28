using FluentAssertions;

using Microsoft.Extensions.Options;

using TripMate.Application.Features.TripAlerts.Common;
using TripMate.Application.Features.TripAlerts.Weather;
using TripMate.Domain.Enums;

namespace TripMate.Application.UnitTests.Features.TripAlerts;

public sealed class WeatherAlertClassifierTests
{
    private readonly WeatherAlertClassifier classifier = new(
        Options.Create(new TripAlertOptions()));

    [Theory]
    [InlineData(2.49, WeatherAlertLevel.None)]
    [InlineData(2.5, WeatherAlertLevel.Advisory)]
    [InlineData(9.99, WeatherAlertLevel.Advisory)]
    [InlineData(10, WeatherAlertLevel.Warning)]
    public void RainBoundaries_MatchApprovedThresholds(double rain, WeatherAlertLevel expected)
    {
        classifier.Classify(rain, thunderstorm: false, hasOfficialAlert: false, IndoorOutdoorType.Outdoor)
            .Should().Be(expected);
    }

    [Fact]
    public void ModerateRain_IsIgnoredForIndoorPoi()
    {
        classifier.Classify(5, false, false, IndoorOutdoorType.Indoor)
            .Should().Be(WeatherAlertLevel.None);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void ThunderstormOrOfficialAlert_IsAlwaysWarning(bool thunderstorm, bool officialAlert)
    {
        classifier.Classify(0, thunderstorm, officialAlert, IndoorOutdoorType.Indoor)
            .Should().Be(WeatherAlertLevel.Warning);
    }
}