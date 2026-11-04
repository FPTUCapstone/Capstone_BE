using FluentAssertions;

using TripMate.Infrastructure.Weather;

namespace TripMate.Infrastructure.UnitTests.Weather;

public sealed class TomorrowIoOptionsValidatorTests
{
    private readonly TomorrowIoOptionsValidator _validator = new();

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Validate_EnabledFlag_ReturnsSuccess(bool enabled)
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
