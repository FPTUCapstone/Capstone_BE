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
        options.Enabled.Should().BeFalse();
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

    // Background mode (specs/UC-10-background-explanation-spec.md, B2 and B7).

    [Fact]
    public void Defaults_BackgroundModeIsOffWithAHundredSecondBudget()
    {
        var options = new ItineraryExplanationExecutionOptions();

        options.BackgroundEnabled.Should().BeFalse();
        options.BackgroundTimeout.Should().Be(TimeSpan.FromSeconds(100));
        ItineraryExplanationExecutionOptions.DefaultBackgroundTimeout.Should().Be(TimeSpan.FromSeconds(100));
        new ItineraryExplanationExecutionOptionsValidator().Validate(null, options).Succeeded.Should().BeTrue();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    [InlineData(301)]
    public void Validate_BackgroundTimeoutOutOfRange_Fails(int seconds)
    {
        var options = new ItineraryExplanationExecutionOptions
        {
            BackgroundTimeout = TimeSpan.FromSeconds(seconds),
        };

        var result = new ItineraryExplanationExecutionOptionsValidator()
            .Validate(null, options);

        result.Failed.Should().BeTrue();
        result.FailureMessage.Should().Contain(nameof(options.BackgroundTimeout));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(300)]
    public void Validate_BackgroundTimeoutAtTheEdges_Succeeds(int seconds)
    {
        var options = new ItineraryExplanationExecutionOptions
        {
            BackgroundEnabled = true,
            BackgroundTimeout = TimeSpan.FromSeconds(seconds),
        };

        new ItineraryExplanationExecutionOptionsValidator().Validate(null, options).Succeeded.Should().BeTrue();
    }

    [Fact]
    public void Validate_BackgroundTimeoutMayExceedTheInlineLimit()
    {
        // The 30-second ceiling protects the request the traveler waits on; the background budget is separate.
        var options = new ItineraryExplanationExecutionOptions
        {
            ProviderTimeout = TimeSpan.FromSeconds(10),
            OverallTimeout = TimeSpan.FromSeconds(20),
            BackgroundTimeout = TimeSpan.FromSeconds(100),
        };

        new ItineraryExplanationExecutionOptionsValidator().Validate(null, options).Succeeded.Should().BeTrue();
    }
}