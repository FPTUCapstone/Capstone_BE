using FluentAssertions;

using TripMate.Infrastructure.Routing;

namespace TripMate.Infrastructure.UnitTests.Routing;

public sealed class OpenRouteServiceOptionsValidatorTests
{
    private readonly OpenRouteServiceOptionsValidator _validator = new();

    [Theory]
    [InlineData(OpenRouteServiceOptions.MinMotorbikeDurationFactor)]
    [InlineData(OpenRouteServiceOptions.DefaultMotorbikeDurationFactor)]
    [InlineData(OpenRouteServiceOptions.MaxMotorbikeDurationFactor)]
    public void Validate_MotorbikeFactorInRange_Succeeds(double factor)
    {
        _validator.Validate(null, new OpenRouteServiceOptions { MotorbikeDurationFactor = factor })
            .Succeeded.Should().BeTrue();
    }

    [Theory]
    [InlineData(0.4)]
    [InlineData(1.6)]
    [InlineData(double.NaN)]
    public void Validate_MotorbikeFactorOutOfRange_FailsWithItsName(double factor)
    {
        var result = _validator.Validate(null, new OpenRouteServiceOptions { MotorbikeDurationFactor = factor });

        result.Failed.Should().BeTrue();
        result.FailureMessage.Should().Contain(nameof(OpenRouteServiceOptions.MotorbikeDurationFactor));
    }
}