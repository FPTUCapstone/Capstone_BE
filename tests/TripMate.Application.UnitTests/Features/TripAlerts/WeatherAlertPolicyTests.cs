using FluentAssertions;

using Microsoft.Extensions.Options;

using TripMate.Application.Features.TripAlerts.Common;
using TripMate.Application.Features.TripAlerts.Weather;
using TripMate.Domain.Enums;

namespace TripMate.Application.UnitTests.Features.TripAlerts;

public sealed class WeatherAlertPolicyTests
{
    private readonly WeatherAlertPolicy policy = new(Options.Create(new TripAlertOptions()));

    [Theory]
    [InlineData(2.49, 30, WeatherAlertAction.None)]
    [InlineData(2.5, 180, WeatherAlertAction.TransientAdvisory)]
    [InlineData(9.99, 61, WeatherAlertAction.TransientAdvisory)]
    [InlineData(2.5, 60, WeatherAlertAction.PersistedAdvisory)]
    [InlineData(10, 180, WeatherAlertAction.Warning)]
    [InlineData(50, 180, WeatherAlertAction.Warning)]
    [InlineData(50, 181, WeatherAlertAction.None)]
    public void ForecastWindows_UseApprovedThresholds(double rain, int minutes,
        WeatherAlertAction expected)
    {
        policy.Decide(rain, false, IndoorOutdoorType.Outdoor, true,
            TimeSpan.FromMinutes(minutes), TripWeatherBasis.ForecastAtPlannedTime)
            .Should().Be(expected);
    }

    [Fact]
    public void UnreviewedOutdoor_DoesNotTriggerRainOnlyWarning()
    {
        policy.Decide(20, false, IndoorOutdoorType.Outdoor, false,
            TimeSpan.FromMinutes(20), TripWeatherBasis.ForecastAtPlannedTime)
            .Should().Be(WeatherAlertAction.None);
        policy.Decide(50, false, IndoorOutdoorType.Outdoor, false,
            TimeSpan.FromMinutes(20), TripWeatherBasis.ForecastAtPlannedTime)
            .Should().Be(WeatherAlertAction.Warning);
    }

    [Fact]
    public void OverdueStop_OnlyFreshRealtimeHeavyConditionsCanWarn()
    {
        policy.Decide(20, false, IndoorOutdoorType.Outdoor, true,
            TimeSpan.FromMinutes(-5), TripWeatherBasis.ForecastAtPlannedTime)
            .Should().Be(WeatherAlertAction.None);
        policy.Decide(5, false, IndoorOutdoorType.Outdoor, true,
            TimeSpan.FromMinutes(-5), TripWeatherBasis.CurrentAtDestination)
            .Should().Be(WeatherAlertAction.None);
        policy.Decide(20, false, IndoorOutdoorType.Outdoor, true,
            TimeSpan.FromMinutes(-5), TripWeatherBasis.CurrentAtDestination)
            .Should().Be(WeatherAlertAction.Warning);
    }
}