using FluentAssertions;

using TripMate.Infrastructure.TripAlerts;

namespace TripMate.Infrastructure.UnitTests.TripAlerts;

public sealed class TripAlertStartupDiagnosticsTests
{
    [Fact]
    public void Warnings_WeatherAndPushReady_ReturnsNothing()
    {
        TripAlertStartupDiagnostics.Warnings(weatherEnabled: true, pushReady: true)
            .Should().BeEmpty();
    }

    [Fact]
    public void Warnings_WeatherDisabled_NamesTheSettingToTurnOn()
    {
        TripAlertStartupDiagnostics.Warnings(weatherEnabled: false, pushReady: true)
            .Should().ContainSingle().Which.Should().Contain("Tomorrow__Enabled");
    }

    [Fact]
    public void Warnings_PushNotReady_NamesTheCredentialSetting()
    {
        TripAlertStartupDiagnostics.Warnings(weatherEnabled: true, pushReady: false)
            .Should().ContainSingle().Which.Should().Contain("GOOGLE_APPLICATION_CREDENTIALS");
    }

    [Fact]
    public void Warnings_BothMissing_ReportsBoth()
    {
        TripAlertStartupDiagnostics.Warnings(weatherEnabled: false, pushReady: false)
            .Should().HaveCount(2);
    }
}