using FluentAssertions;

using Microsoft.Extensions.Options;

using TripMate.Application.Features.Scheduling.Explanation;

namespace TripMate.Application.UnitTests.Features.Scheduling.Explanation;

public sealed class ItineraryExplanationExecutionOptionsTests
{
    [Fact]
    public void Defaults_ProviderTimeoutIsExactlyFiveSeconds()
    {
        var options = new ItineraryExplanationExecutionOptions();

        ItineraryExplanationExecutionOptions.DefaultProviderTimeout.Should().Be(
            TimeSpan.FromSeconds(5));
        options.ProviderTimeout.Should().Be(TimeSpan.FromSeconds(5));
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
}