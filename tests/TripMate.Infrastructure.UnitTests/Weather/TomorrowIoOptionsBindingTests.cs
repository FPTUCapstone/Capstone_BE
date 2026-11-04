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
