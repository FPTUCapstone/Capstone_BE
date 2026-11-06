using FluentAssertions;

using TripMate.Infrastructure.Weather;

namespace TripMate.Infrastructure.UnitTests.Weather;

public sealed class TomorrowIoOptionsValidatorTests
{
    private readonly TomorrowIoOptionsValidator _validator = new();

    [Theory]
    [InlineData(false)]
    public void Validate_DisabledWithoutCredentials_ReturnsSuccess(bool enabled)
    {
        var options = new TomorrowIoOptions
        {
            Enabled = enabled,
            WebhookToleranceSeconds = 300,
            MaxPayloadSizeBytes = 1048576,
            ReconciliationIntervalMinutes = 15,
            LocationSyncIntervalHours = 6
        };

        var result = _validator.Validate(null, options);

        result.Succeeded.Should().BeTrue();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Validate_InvalidWebhookToleranceSeconds_ReturnsFail(int seconds)
    {
        var options = new TomorrowIoOptions
        {
            Enabled = false,
            WebhookToleranceSeconds = seconds
        };

        var result = _validator.Validate(null, options);

        result.Failed.Should().BeTrue();
        result.FailureMessage.Should().Contain(nameof(TomorrowIoOptions.WebhookToleranceSeconds));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-100)]
    public void Validate_InvalidMaxPayloadSizeBytes_ReturnsFail(int bytes)
    {
        var options = new TomorrowIoOptions
        {
            Enabled = false,
            MaxPayloadSizeBytes = bytes
        };

        var result = _validator.Validate(null, options);

        result.Failed.Should().BeTrue();
        result.FailureMessage.Should().Contain(nameof(TomorrowIoOptions.MaxPayloadSizeBytes));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void Validate_InvalidReconciliationIntervalMinutes_ReturnsFail(int minutes)
    {
        var options = new TomorrowIoOptions
        {
            Enabled = false,
            ReconciliationIntervalMinutes = minutes
        };

        var result = _validator.Validate(null, options);

        result.Failed.Should().BeTrue();
        result.FailureMessage.Should().Contain(nameof(TomorrowIoOptions.ReconciliationIntervalMinutes));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-2)]
    public void Validate_InvalidLocationSyncIntervalHours_ReturnsFail(int hours)
    {
        var options = new TomorrowIoOptions
        {
            Enabled = false,
            LocationSyncIntervalHours = hours
        };

        var result = _validator.Validate(null, options);

        result.Failed.Should().BeTrue();
        result.FailureMessage.Should().Contain(nameof(TomorrowIoOptions.LocationSyncIntervalHours));
    }
}

public sealed class TomorrowIoEnabledOptionsValidatorTests
{
    private static TomorrowIoOptions Enabled() => new()
    {
        Enabled = true,
        ApiKey = "test-api-key",
        WebhookSecret = "test-webhook-secret",
        AlertId = "alert-123",
        BaseUrl = "https://api.tomorrow.io",
    };

    [Fact]
    public void CompleteEnabledConfiguration_IsValid()
    {
        new TomorrowIoOptionsValidator().Validate(null, Enabled()).Succeeded.Should().BeTrue();
    }

    [Theory]
    [InlineData(nameof(TomorrowIoOptions.ApiKey))]
    [InlineData(nameof(TomorrowIoOptions.WebhookSecret))]
    [InlineData(nameof(TomorrowIoOptions.AlertId))]
    public void EnabledWithoutARequiredSetting_FailsAndNamesOnlyTheSetting(string missing)
    {
        var options = Enabled();
        typeof(TomorrowIoOptions).GetProperty(missing)!.SetValue(options, " ");

        var result = new TomorrowIoOptionsValidator().Validate(null, options);

        result.Failed.Should().BeTrue();
        result.FailureMessage.Should().Contain(missing).And.NotContain("test-api-key").And.NotContain("test-webhook-secret");
    }

    [Theory]
    [InlineData("")]
    [InlineData("api.tomorrow.io")]
    [InlineData("http://api.tomorrow.io")]
    public void EnabledWithAnInvalidBaseUrl_Fails(string baseUrl)
    {
        var options = Enabled();
        options.BaseUrl = baseUrl;

        new TomorrowIoOptionsValidator().Validate(null, options).FailureMessage.Should().Contain(nameof(TomorrowIoOptions.BaseUrl));
    }

    [Theory]
    [InlineData(nameof(TomorrowIoOptions.ReconciliationLookbackMinutes))]
    [InlineData(nameof(TomorrowIoOptions.LocationMonitoringLookaheadDays))]
    [InlineData(nameof(TomorrowIoOptions.SafetyReviewAcknowledgementSlaMinutes))]
    public void NonPositiveInterval_Fails(string setting)
    {
        var options = Enabled();
        typeof(TomorrowIoOptions).GetProperty(setting)!.SetValue(options, 0);

        new TomorrowIoOptionsValidator().Validate(null, options).FailureMessage.Should().Contain(setting);
    }

    [Fact]
    public void DisabledIntegration_NeedsNoCredentials()
    {
        new TomorrowIoOptionsValidator().Validate(null, new TomorrowIoOptions { Enabled = false }).Succeeded.Should().BeTrue();
    }
}
