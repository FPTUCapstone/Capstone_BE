using FluentAssertions;

using MediatR;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

using TripMate.Application;
using TripMate.Application.Common.Models;
using TripMate.Application.Features.Scheduling.Common;
using TripMate.Application.Features.Scheduling.Create;
using TripMate.Application.Features.Scheduling.Personalization;
using TripMate.Infrastructure.AiRanking;

namespace TripMate.Infrastructure.UnitTests.AiRanking;

public sealed class AiRankingDependencyInjectionTests
{
    [Fact]
    public void AddInfrastructure_DisabledWithoutProviderCredentials_ValidatesAndResolvesRankingGraph()
    {
        IConfiguration configuration = BuildConfiguration(new Dictionary<string, string?>
        {
            ["AiRanking:Enabled"] = "false",
        });
        var services = new ServiceCollection();
        services.AddApplication();
        services.AddInfrastructure(configuration);
        var httpHandler = new RecordingHttpMessageHandler();
        services.AddHttpClient<IPoiRankingProvider, HttpPoiRankingProvider>()
            .ConfigurePrimaryHttpMessageHandler(() => httpHandler);
        using ServiceProvider provider = services.BuildServiceProvider();

        provider.GetRequiredService<IStartupValidator>().Validate();
        using IServiceScope scope = provider.CreateScope();

        provider.GetRequiredService<IOptions<PoiRankingProviderOptions>>()
            .Value.Enabled.Should().BeFalse();
        scope.ServiceProvider.GetRequiredService<IPoiRankingProvider>()
            .Should().BeOfType<HttpPoiRankingProvider>();
        scope.ServiceProvider.GetRequiredService<PoiRankingOrchestrator>()
            .Should().NotBeNull();
        scope.ServiceProvider.GetRequiredService<
                IRequestHandler<CreateSchedulingRequestCommand, Result<SchedulingResponseDto>>>()
            .Should().NotBeNull();
        httpHandler.CallCount.Should().Be(0);
    }
    [Theory]
    [InlineData("AiRanking:ApiKey", null, nameof(PoiRankingProviderOptions.ApiKey))]
    [InlineData("AiRanking:Endpoint", null, nameof(PoiRankingProviderOptions.Endpoint))]
    [InlineData("AiRanking:Endpoint", "http://provider.test/interactions", nameof(PoiRankingProviderOptions.Endpoint))]
    [InlineData("AiRanking:ModelName", " ", nameof(PoiRankingProviderOptions.ModelName))]
    public void AddInfrastructure_EnabledWithInvalidProviderConfiguration_FailsStartupValidation(
        string invalidKey,
        string? invalidValue,
        string expectedProperty)
    {
        var values = ValidEnabledProviderConfiguration();
        values[invalidKey] = invalidValue;
        using ServiceProvider provider = BuildProvider(values);

        var action = () => provider.GetRequiredService<IStartupValidator>().Validate();

        action.Should().Throw<OptionsValidationException>()
            .Which.Message.Should().Contain(expectedProperty)
            .And.NotContain("unit-test-provider-key");
    }

    [Fact]
    public void AddInfrastructure_EnabledWithValidSafeConfiguration_PassesStartupValidation()
    {
        using ServiceProvider provider = BuildProvider(ValidEnabledProviderConfiguration());

        var action = () => provider.GetRequiredService<IStartupValidator>().Validate();

        action.Should().NotThrow();
        provider.GetRequiredService<IOptions<PoiRankingProviderOptions>>()
            .Value.Enabled.Should().BeTrue();
    }

    [Theory]
    [InlineData("Personalization:CategoryAffinityWeight", "0.400", "Base component weights")]
    [InlineData("Personalization:BaseWeight", "0.70", "BaseWeight and AiWeight")]
    [InlineData("Personalization:ProviderTimeout", "00:00:00", nameof(PersonalizationRankingOptions.ProviderTimeout))]
    [InlineData("Personalization:MaxProviderCandidates", "39", nameof(PersonalizationRankingOptions.MaxProviderCandidates))]
    public void AddInfrastructure_InvalidPersonalizationConfiguration_FailsStartupValidation(
        string invalidKey,
        string invalidValue,
        string expectedFailure)
    {
        using ServiceProvider provider = BuildProvider(new Dictionary<string, string?>
        {
            [invalidKey] = invalidValue,
        });

        var action = () => provider.GetRequiredService<IStartupValidator>().Validate();

        action.Should().Throw<OptionsValidationException>()
            .Which.Message.Should().Contain(expectedFailure);
    }

    [Fact]
    public void CommittedConfiguration_UsesCanonicalDefaultsWithoutApiKey()
    {
        IConfiguration configuration = new ConfigurationBuilder()
            .AddJsonFile(FindRepositoryFile("src", "TripMate.Api", "appsettings.json"))
            .AddInMemoryCollection(BaselineConfiguration())
            .Build();
        var services = new ServiceCollection();
        services.AddInfrastructure(configuration);
        using ServiceProvider provider = services.BuildServiceProvider();

        PersonalizationRankingOptions personalization = provider
            .GetRequiredService<IOptions<PersonalizationRankingOptions>>()
            .Value;
        PoiRankingProviderOptions aiRanking = provider
            .GetRequiredService<IOptions<PoiRankingProviderOptions>>()
            .Value;

        var startupAction = () => provider.GetRequiredService<IStartupValidator>().Validate();

        startupAction.Should().NotThrow();
        personalization.Should().BeEquivalentTo(new PersonalizationRankingOptions());
        aiRanking.Enabled.Should().BeFalse();
        aiRanking.Endpoint.Should().Be(
            "https://generativelanguage.googleapis.com/v1/interactions");
        aiRanking.ModelName.Should().Be("gemini-3.5-flash-lite");
        aiRanking.ApiKey.Should().BeNull();
        configuration["AiRanking:ApiKey"].Should().BeNull();
    }

    private static ServiceProvider BuildProvider(
        IEnumerable<KeyValuePair<string, string?>> values)
    {
        var services = new ServiceCollection();
        services.AddInfrastructure(BuildConfiguration(values));
        return services.BuildServiceProvider();
    }

    private static Dictionary<string, string?> ValidEnabledProviderConfiguration() => new()
    {
        ["AiRanking:Enabled"] = "true",
        ["AiRanking:Endpoint"] = "https://provider.test/v1/interactions",
        ["AiRanking:ApiKey"] = "unit-test-provider-key",
        ["AiRanking:ModelName"] = "unit-test-model",
    };

    private static IConfiguration BuildConfiguration(
        IEnumerable<KeyValuePair<string, string?>> values) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(BaselineConfiguration().Concat(values))
            .Build();

    private static IEnumerable<KeyValuePair<string, string?>> BaselineConfiguration()
    {
        yield return new("ConnectionStrings:Default", "Server=unused;Database=unused;");
        yield return new("Cloudinary:CloudName", "test-cloud");
        yield return new("Cloudinary:ApiKey", "test-key");
        yield return new("Cloudinary:ApiSecret", "test-secret");
        yield return new("Cloudinary:TourMediaFolderRoot", "tripmate/tests/tours");
    }

    private static string FindRepositoryFile(params string[] relativeSegments)
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            string candidate = Path.Combine(
                [directory.FullName, .. relativeSegments]);
            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new FileNotFoundException(
            $"Could not locate {Path.Combine(relativeSegments)} from the test output directory.");
    }

    private sealed class RecordingHttpMessageHandler : HttpMessageHandler
    {
        public int CallCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            CallCount++;
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK));
        }
    }
}