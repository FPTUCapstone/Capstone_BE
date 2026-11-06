using FluentAssertions;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

using TripMate.Infrastructure.Weather;

namespace TripMate.Infrastructure.UnitTests.Weather;

public sealed class TomorrowIoOptionsBindingTests
{
    [Fact]
    public void AddInfrastructure_WhenTomorrowIoApiKeyIsMissing_UsesLegacyTomorrowApiKey()
    {
        const string legacyApiKey = "legacy-test-key";
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Tomorrow:ApiKey"] = legacyApiKey,
            })
            .Build();
        var services = new ServiceCollection();

        services.AddInfrastructure(configuration);
        using ServiceProvider provider = services.BuildServiceProvider();

        provider.GetRequiredService<IOptions<TomorrowIoOptions>>()
            .Value.ApiKey.Should().Be(legacyApiKey);
    }
}

public sealed class TomorrowIoOptionsLegacyKeyValidationTests
{
    [Fact]
    public void EnabledWithOnlyTheLegacyApiKey_PassesValidation()
    {
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Tomorrow:ApiKey"] = "legacy-test-key",
                ["TomorrowIo:Enabled"] = "true",
                ["TomorrowIo:WebhookSecret"] = "test-secret",
                ["TomorrowIo:AlertId"] = "alert-123",
            })
            .Build();
        var services = new ServiceCollection();

        services.AddInfrastructure(configuration);
        using ServiceProvider provider = services.BuildServiceProvider();

        // Value runs PostConfigure (the legacy fallback) before the validator.
        var options = provider.GetRequiredService<IOptions<TomorrowIoOptions>>().Value;
        options.ApiKey.Should().Be("legacy-test-key");
        options.AlertId.Should().Be("alert-123");
    }
}