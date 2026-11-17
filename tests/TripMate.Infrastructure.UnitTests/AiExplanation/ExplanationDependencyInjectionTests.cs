using FluentAssertions;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

using TripMate.Application.Features.Scheduling.Explanation;
using TripMate.Infrastructure.Ai;
using TripMate.Infrastructure.AiExplanation;
using TripMate.Infrastructure.UnitTests.Security;

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
        options.Provider.Should().Be(AiProviderKind.TripMateAi);
        options.Endpoint.Should().Be("http://tripmate-ai:8000/v1/explain-itinerary");
        options.ModelName.Should().Be("tripmate-explainer");
        options.ApiKey.Should().BeNull();
        options.TimeoutSeconds.Should().Be(10);
        options.OverallTimeoutSeconds.Should().Be(20);
        options.MaxAttempts.Should().Be(2);
        options.RetryBaseDelayMilliseconds.Should().Be(500);
        configuration["AiExplanation:ApiKey"].Should().BeNull();
        configuration["AiExplanation:Timeout"].Should().BeNull();
        provider.GetRequiredService<IOptions<ItineraryExplanationExecutionOptions>>()
            .Value.Enabled.Should().BeFalse();

        // Background mode is committed switched off, with the budget written out so operators see the knob.
        configuration["AiExplanation:BackgroundEnabled"].Should().Be("False");
        configuration["AiExplanation:BackgroundTimeoutSeconds"].Should().Be("100");
        options.BackgroundEnabled.Should().BeFalse();
        options.BackgroundTimeoutSeconds.Should().Be(100);
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

    // Background mode (specs/UC-10-background-explanation-spec.md, B2 and B7).

    [Fact]
    public void AddInfrastructure_WithoutBackgroundSettings_KeepsBackgroundModeOff()
    {
        using ServiceProvider provider = BuildProvider(new Dictionary<string, string?>
        {
            ["AiExplanation:Enabled"] = "false",
        });

        provider.GetRequiredService<IStartupValidator>().Validate();
        var executionOptions = provider
            .GetRequiredService<IOptions<ItineraryExplanationExecutionOptions>>()
            .Value;
        executionOptions.BackgroundEnabled.Should().BeFalse();
        executionOptions.BackgroundTimeout.Should().Be(TimeSpan.FromSeconds(100));
    }

    [Fact]
    public void AddInfrastructure_MapsBackgroundModeOntoTheExecutionOptions()
    {
        using ServiceProvider provider = BuildProvider(EnabledProvider().Concat(
        [
            new KeyValuePair<string, string?>("AiExplanation:BackgroundEnabled", "true"),
            new KeyValuePair<string, string?>("AiExplanation:BackgroundTimeoutSeconds", "120"),
        ]));

        provider.GetRequiredService<IStartupValidator>().Validate();
        var executionOptions = provider
            .GetRequiredService<IOptions<ItineraryExplanationExecutionOptions>>()
            .Value;
        executionOptions.Enabled.Should().BeTrue();
        executionOptions.BackgroundEnabled.Should().BeTrue();
        executionOptions.BackgroundTimeout.Should().Be(TimeSpan.FromSeconds(120));
        // The inline limits are untouched by the background budget.
        executionOptions.OverallTimeout.Should().Be(ItineraryExplanationExecutionOptions.DefaultOverallTimeout);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("301")]
    public void AddInfrastructure_BackgroundTimeoutOutOfRange_FailsStartupValidation(string seconds)
    {
        using ServiceProvider provider = BuildProvider(new Dictionary<string, string?>
        {
            ["AiExplanation:Enabled"] = "false",
            ["AiExplanation:BackgroundTimeoutSeconds"] = seconds,
        });

        var action = () => provider.GetRequiredService<IStartupValidator>().Validate();

        action.Should().Throw<OptionsValidationException>()
            .WithMessage($"*{nameof(ItineraryExplanationExecutionOptions.BackgroundTimeout)}*");
    }

    [Theory]
    [InlineData("Gemini")]
    [InlineData("TripMateAi")]
    public void AddInfrastructure_ExplanationHttpClientHasNoTimeoutOfItsOwn(string providerKind)
    {
        // HttpClient's default 100 seconds would silently cut a longer background budget; the provider's own
        // cancellation token enforces every limit instead.
        using ServiceProvider provider = BuildProvider(new Dictionary<string, string?>
        {
            ["AiExplanation:Enabled"] = "false",
            ["AiExplanation:Provider"] = providerKind,
        });

        using HttpClient client = provider
            .GetRequiredService<IHttpClientFactory>()
            .CreateClient(nameof(IItineraryExplanationProvider));

        client.Timeout.Should().Be(Timeout.InfiniteTimeSpan);
    }

    [Fact]
    public void AddInfrastructure_RegistersTheWaitingListItsWorkerAndTheProcessor()
    {
        var services = new ServiceCollection();
        services.AddInfrastructure(new ConfigurationBuilder()
            .AddInMemoryCollection(BaselineConfiguration())
            .Build());

        services.Should().Contain(descriptor =>
            descriptor.ServiceType == typeof(IHostedService)
            && descriptor.ImplementationType == typeof(ItineraryExplanationBackgroundService));
        services.Should().Contain(descriptor =>
            descriptor.ServiceType == typeof(IBackgroundItineraryExplanationProcessor)
            && descriptor.Lifetime == ServiceLifetime.Scoped);
        using ServiceProvider provider = services.BuildServiceProvider();

        // One list for the whole process: what a create request queues is what the worker reads.
        provider.GetRequiredService<IItineraryExplanationQueue>()
            .Should().BeSameAs(provider.GetRequiredService<InMemoryItineraryExplanationQueue>());
        using IServiceScope scope = provider.CreateScope();
        scope.ServiceProvider.GetRequiredService<IBackgroundItineraryExplanationProcessor>()
            .Should().BeOfType<BackgroundItineraryExplanationProcessor>();
    }

    private static IEnumerable<KeyValuePair<string, string?>> EnabledProvider() =>
    [
        new("AiExplanation:Enabled", "true"),
        new("AiExplanation:Endpoint", "https://generativelanguage.googleapis.com/v1/interactions"),
        new("AiExplanation:ApiKey", "test-key"),
        new("AiExplanation:ModelName", "test-model"),
    ];

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
        yield return new(TicketQrTestKeys.EncryptionKeySetting, TicketQrTestKeys.EncryptionKey);
        yield return new(TicketQrTestKeys.SigningKeySetting, TicketQrTestKeys.SigningKey);
        yield return new("Cloudinary:TourMediaFolderRoot", "tripmate/tests/tours");
        yield return new("Cloudinary:OperatorDocumentsFolderRoot", "tripmate/tests/operator-documents");
        yield return new("Cloudinary:RevenueExportsFolderRoot", "tripmate/tests/revenue-exports");
        yield return new("SchedulingGeneration:Personalization:Version", "2026.1");
        yield return new("SchedulingGeneration:Personalization:Pace:Relaxed:LambdaPerMin", "0.025");
        yield return new("SchedulingGeneration:Personalization:Pace:Relaxed:MaxStopsPerDay", "3");
        yield return new("SchedulingGeneration:Personalization:Pace:Relaxed:MaxDailyEffort", "200");
        yield return new("SchedulingGeneration:Personalization:Pace:Relaxed:RestAfterMin", "90");
        yield return new("SchedulingGeneration:Personalization:Pace:Moderate:LambdaPerMin", "0.015");
        yield return new("SchedulingGeneration:Personalization:Pace:Moderate:MaxStopsPerDay", "5");
        yield return new("SchedulingGeneration:Personalization:Pace:Moderate:MaxDailyEffort", "300");
        yield return new("SchedulingGeneration:Personalization:Pace:Moderate:RestAfterMin", "120");
        yield return new("SchedulingGeneration:Personalization:Pace:Fast:LambdaPerMin", "0.008");
        yield return new("SchedulingGeneration:Personalization:Pace:Fast:MaxStopsPerDay", "8");
        yield return new("SchedulingGeneration:Personalization:Pace:Fast:MaxDailyEffort", "450");
        yield return new("SchedulingGeneration:Personalization:Pace:Fast:RestAfterMin", "180");
        yield return new("SchedulingGeneration:TimeFit:Version", "2026.1");
        yield return new("SchedulingGeneration:Shadow:Enabled", "false");
        yield return new("SchedulingGeneration:Shadow:SampleRate", "1.0");
        yield return new("SchedulingGeneration:Shadow:TimeoutMs", "5000");
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