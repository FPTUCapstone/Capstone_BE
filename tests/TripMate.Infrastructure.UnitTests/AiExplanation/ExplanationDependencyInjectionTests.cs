using FluentAssertions;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

using TripMate.Application.Features.Scheduling.Explanation;
using TripMate.Infrastructure.AiExplanation;

namespace TripMate.Infrastructure.UnitTests.AiExplanation;

public sealed class ExplanationDependencyInjectionTests
{
    [Fact]
    public void AddInfrastructure_DisabledWithoutProviderCredentials_PassesStartupValidation()
    {
        using ServiceProvider provider = BuildProvider(new Dictionary<string, string?>
        {
            ["AiExplanation:Enabled"] = "false",
        });

        var action = () => provider.GetRequiredService<IStartupValidator>().Validate();

        action.Should().NotThrow();
        provider.GetRequiredService<IOptions<ExplanationProviderOptions>>()
            .Value.Enabled.Should().BeFalse();
        provider.GetRequiredService<IItineraryExplanationProvider>()
            .Should().BeOfType<HttpItineraryExplanationProvider>();
        var executionOptions = provider
            .GetRequiredService<IOptions<ItineraryExplanationExecutionOptions>>()
            .Value;
        executionOptions.Enabled.Should().BeFalse();
        executionOptions.ProviderTimeout.Should().Be(
            ItineraryExplanationExecutionOptions.DefaultProviderTimeout);
        executionOptions.OverallTimeout.Should().Be(
            ItineraryExplanationExecutionOptions.DefaultOverallTimeout);
    }

    [Fact]
    public void CommittedApiConfiguration_DisabledWithoutSecret_PassesBootValidation()
    {
        IConfiguration configuration = new ConfigurationBuilder()
            .AddJsonFile(FindRepositoryFile("src", "TripMate.Api", "appsettings.json"))
            .AddInMemoryCollection(BaselineConfiguration())
            .Build();
        var services = new ServiceCollection();
        services.AddInfrastructure(configuration);
        using ServiceProvider provider = services.BuildServiceProvider();

        var action = () => provider.GetRequiredService<IStartupValidator>().Validate();

        action.Should().NotThrow();
        var options = provider.GetRequiredService<IOptions<ExplanationProviderOptions>>().Value;
        options.Enabled.Should().BeFalse();
        options.Endpoint.Should().Be(
            "https://generativelanguage.googleapis.com/v1/interactions");
        options.ModelName.Should().Be("gemini-3.5-flash-lite");
        options.ApiKey.Should().BeNull();
        options.TimeoutSeconds.Should().Be(10);
        options.OverallTimeoutSeconds.Should().Be(20);
        options.MaxAttempts.Should().Be(2);
        options.RetryBaseDelayMilliseconds.Should().Be(500);
        configuration["AiExplanation:ApiKey"].Should().BeNull();
        configuration["AiExplanation:Timeout"].Should().BeNull();
        provider.GetRequiredService<IOptions<ItineraryExplanationExecutionOptions>>()
            .Value.Enabled.Should().BeFalse();
    }

    [Fact]
    public void AddInfrastructure_EnabledMapsFeatureStateWithoutRegisteringRawBoolean()
    {
        var services = new ServiceCollection();
        services.AddInfrastructure(new ConfigurationBuilder()
            .AddInMemoryCollection(BaselineConfiguration().Concat(
            [
                new KeyValuePair<string, string?>("AiExplanation:Enabled", "true"),
                new KeyValuePair<string, string?>(
                    "AiExplanation:Endpoint",
                    "https://generativelanguage.googleapis.com/v1/interactions"),
                new KeyValuePair<string, string?>("AiExplanation:ApiKey", "test-key"),
                new KeyValuePair<string, string?>("AiExplanation:ModelName", "test-model"),
            ]))
            .Build());

        services.Should().NotContain(descriptor => descriptor.ServiceType == typeof(bool));
        using ServiceProvider provider = services.BuildServiceProvider();

        provider.GetRequiredService<IStartupValidator>().Validate();
        provider.GetRequiredService<IOptions<ItineraryExplanationExecutionOptions>>()
            .Value.Enabled.Should().BeTrue();
    }

    private static ServiceProvider BuildProvider(
        IEnumerable<KeyValuePair<string, string?>> values)
    {
        var services = new ServiceCollection();
        services.AddInfrastructure(new ConfigurationBuilder()
            .AddInMemoryCollection(BaselineConfiguration().Concat(values))
            .Build());
        return services.BuildServiceProvider();
    }

    private static IEnumerable<KeyValuePair<string, string?>> BaselineConfiguration()
    {
        yield return new("ConnectionStrings:Default", "Server=unused;Database=unused;");
        yield return new("Cloudinary:CloudName", "test-cloud");
        yield return new("Cloudinary:ApiKey", "test-key");
        yield return new("Cloudinary:ApiSecret", "test-secret");
        yield return new("Cloudinary:TourMediaFolderRoot", "tripmate/tests/tours");
        yield return new("Cloudinary:OperatorDocumentsFolderRoot", "tripmate/tests/operator-documents");
    }

    private static string FindRepositoryFile(params string[] relativeSegments)
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            string candidate = Path.Combine([directory.FullName, .. relativeSegments]);
            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new FileNotFoundException(
            $"Could not locate {Path.Combine(relativeSegments)} from the test output directory.");
    }
}