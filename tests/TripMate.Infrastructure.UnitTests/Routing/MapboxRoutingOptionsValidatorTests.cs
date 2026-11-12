using FluentAssertions;

using TripMate.Infrastructure.Routing;

namespace TripMate.Infrastructure.UnitTests.Routing;

public sealed class MapboxRoutingOptionsValidatorTests
{
    private readonly MapboxRoutingOptionsValidator _validator = new();

    [Theory]
    [InlineData(MapboxRoutingOptions.MinMotorbikeDurationFactor)]
    [InlineData(MapboxRoutingOptions.DefaultMotorbikeDurationFactor)]
    [InlineData(MapboxRoutingOptions.MaxMotorbikeDurationFactor)]
    public void Validate_MotorbikeFactorInRange_Succeeds(double factor)
    {
        _validator.Validate(null, new MapboxRoutingOptions { MotorbikeDurationFactor = factor })
            .Succeeded.Should().BeTrue();
    }

    [Theory]
    [InlineData(0.4)]
    [InlineData(1.6)]
    [InlineData(double.NaN)]
    public void Validate_MotorbikeFactorOutOfRange_FailsWithItsName(double factor)
    {
        var result = _validator.Validate(null, new MapboxRoutingOptions { MotorbikeDurationFactor = factor });

        result.Failed.Should().BeTrue();
        result.FailureMessage.Should().Contain(nameof(MapboxRoutingOptions.MotorbikeDurationFactor));
    }

    [Theory]
    [InlineData("https://api.mapbox.com/")]
    [InlineData("http://localhost:8080/")]
    public void Validate_AbsoluteHttpBaseUrl_Succeeds(string baseUrl)
    {
        _validator.Validate(null, new MapboxRoutingOptions { BaseUrl = baseUrl })
            .Succeeded.Should().BeTrue();
    }

    [Theory]
    [InlineData("")]
    [InlineData("api.mapbox.com")]
    [InlineData("/relative/")]
    [InlineData("ftp://api.mapbox.com/")]
    public void Validate_BaseUrlNotAbsoluteHttp_FailsWithItsName(string baseUrl)
    {
        var result = _validator.Validate(null, new MapboxRoutingOptions { BaseUrl = baseUrl });

        result.Failed.Should().BeTrue();
        result.FailureMessage.Should().Contain(nameof(MapboxRoutingOptions.BaseUrl));
    }
}