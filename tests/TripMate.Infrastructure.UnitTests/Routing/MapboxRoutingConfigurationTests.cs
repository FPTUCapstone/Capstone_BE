using FluentAssertions;

using Microsoft.Extensions.Configuration;

using TripMate.Infrastructure.Routing;

namespace TripMate.Infrastructure.UnitTests.Routing;

public sealed class MapboxRoutingConfigurationTests
{
    [Fact]
    public void EnvironmentVariableNames_BindToMapboxRoutingOptions()
    {
        // The real names the developer's .env / Railway use: MAPBOXROUTING__ACCESSTOKEN, __BASEURL, ...
        var variables = new Dictionary<string, string?>
        {
            ["MAPBOXROUTING__ACCESSTOKEN"] = "pk.test-only",
            ["MAPBOXROUTING__BASEURL"] = "https://mapbox.example/",
            ["MAPBOXROUTING__MOTORBIKEDURATIONFACTOR"] = "0.8",
        };
        var previous = variables.Keys.ToDictionary(key => key, Environment.GetEnvironmentVariable);
        try
        {
            foreach (var (key, value) in variables)
            {
                Environment.SetEnvironmentVariable(key, value);
            }

            var configuration = new ConfigurationBuilder().AddEnvironmentVariables().Build();

            var options = configuration.GetSection(MapboxRoutingOptions.SectionName).Get<MapboxRoutingOptions>()!;

            options.AccessToken.Should().Be("pk.test-only");
            options.BaseUrl.Should().Be("https://mapbox.example/");
            options.MotorbikeDurationFactor.Should().Be(0.8);
        }
        finally
        {
            foreach (var (key, value) in previous)
            {
                Environment.SetEnvironmentVariable(key, value);
            }
        }
    }

    [Fact]
    public void Defaults_PointAtMapboxWithNoTokenAndTheOldMotorbikeFactor()
    {
        var options = new MapboxRoutingOptions();

        options.BaseUrl.Should().Be("https://api.mapbox.com/");
        options.AccessToken.Should().BeEmpty();
        options.MotorbikeDurationFactor.Should().Be(MapboxRoutingOptions.DefaultMotorbikeDurationFactor);
    }
}