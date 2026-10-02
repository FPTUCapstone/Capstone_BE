using FluentAssertions;

using Microsoft.Extensions.Options;

using TripMate.Application.Features.Scheduling.Explanation;

namespace TripMate.Application.UnitTests.Features.Scheduling.Explanation;

public sealed class ItineraryExplanationExecutionOptionsTests
{
    [Fact]
    public void Defaults_TimeoutAndRetryValuesMatchConfiguredDefaults()
    {
        var options = new ItineraryExplanationExecutionOptions();

        ItineraryExplanationExecutionOptions.DefaultProviderTimeout.Should().Be(
            TimeSpan.FromSeconds(10));
        ItineraryExplanationExecutionOptions.DefaultOverallTimeout.Should().Be(
            TimeSpan.FromSeconds(20));
        options.ProviderTimeout.Should().Be(TimeSpan.FromSeconds(10));
        options.OverallTimeout.Should().Be(TimeSpan.FromSeconds(20));
        options.MaxAttempts.Should().Be(2);
        options.RetryBaseDelayMilliseconds.Should().Be(500);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Validate_NonPositiveProviderTimeout_Fails(int seconds)
    {
        var options = new ItineraryExplanationExecutionOptions
        {
            ProviderTimeout = TimeSpan.FromSeconds(seconds),
        };

        var result = new ItineraryExplanationExecutionOptionsValidator()
            .Validate(null, options);

        result.Failed.Should().BeTrue();
        result.FailureMessage.Should().Contain(nameof(options.ProviderTimeout));
    }

    [Fact]
    public void Validate_ProviderTimeoutGreaterThanOverallTimeout_Fails()
    {
        var options = new ItineraryExplanationExecutionOptions
        {
            ProviderTimeout = TimeSpan.FromSeconds(15),
            OverallTimeout = TimeSpan.FromSeconds(10),
        };

        var result = new ItineraryExplanationExecutionOptionsValidator()
            .Validate(null, options);

        result.Failed.Should().BeTrue();
        result.FailureMessage.Should().Contain("cannot exceed");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(31)]
    public void Validate_OverallTimeoutOutOfRange_Fails(int seconds)
    {
        var options = new ItineraryExplanationExecutionOptions
        {
            ProviderTimeout = TimeSpan.FromSeconds(1),
            OverallTimeout = TimeSpan.FromSeconds(seconds),
        };

        var result = new ItineraryExplanationExecutionOptionsValidator()
            .Validate(null, options);

        result.Failed.Should().BeTrue();
        result.FailureMessage.Should().Contain(nameof(options.OverallTimeout));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    public void Validate_InvalidMaxAttempts_Fails(int maxAttempts)
    {
        var options = new ItineraryExplanationExecutionOptions
        {
            MaxAttempts = maxAttempts,
        };

        var result = new ItineraryExplanationExecutionOptionsValidator()
            .Validate(null, options);

        result.Failed.Should().BeTrue();
        result.FailureMessage.Should().Contain(nameof(options.MaxAttempts));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(5001)]
    public void Validate_InvalidRetryBaseDelay_Fails(int delayMs)
    {
        var options = new ItineraryExplanationExecutionOptions
        {
            RetryBaseDelayMilliseconds = delayMs,
        };

        var result = new ItineraryExplanationExecutionOptionsValidator()
            .Validate(null, options);

        result.Failed.Should().BeTrue();
        result.FailureMessage.Should().Contain(nameof(options.RetryBaseDelayMilliseconds));
    }
}