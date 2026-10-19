using FluentAssertions;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

using TripMate.Application;
using TripMate.Application.Features.Scheduling.Explanation;
using TripMate.Application.Features.Scheduling.Personalization;
using TripMate.Infrastructure.Ai;
using TripMate.Infrastructure.AiExplanation;
using TripMate.Infrastructure.AiRanking;
using TripMate.Infrastructure.TripMateAi;

namespace TripMate.Infrastructure.UnitTests.TripMateAi;

/// <summary>
/// Provider switch, startup validation and DI registration for the TripMate AI providers
/// (spec UC-10-tripmate-ai-provider §12.2, C1–C8).
/// </summary>
public sealed class TripMateAiConfigurationTests
{
    private const string SharedKey = "test-only-shared-key";
    private const string RankingEndpoint = "http://ai:8000/v1/rank-pois";
    private const string ExplanationEndpoint = "http://ai:8000/v1/explain-itinerary";

    // C1
    [Fact]
    public void ShippedAppSettings_SelectGeminiExplicitlyForBothFeatures()
    {
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(BaselineConfiguration())
            .AddJsonFile(FindRepositoryFile("src", "TripMate.Api", "appsettings.json"), optional: false)
            .Build();

        configuration["AiRanking:Provider"].Should().Be("Gemini");
        configuration["AiExplanation:Provider"].Should().Be("Gemini");

        using ServiceProvider provider = BuildProvider(configuration);
        provider.GetRequiredService<IStartupValidator>().Validate();
        using IServiceScope scope = provider.CreateScope();

        provider.GetRequiredService<IOptions<PoiRankingProviderOptions>>()
            .Value.Provider.Should().Be(AiProviderKind.Gemini);
        provider.GetRequiredService<IOptions<ExplanationProviderOptions>>()
            .Value.Provider.Should().Be(AiProviderKind.Gemini);
        scope.ServiceProvider.GetRequiredService<IPoiRankingProvider>()
            .Should().BeOfType<HttpPoiRankingProvider>();
        scope.ServiceProvider.GetRequiredService<IItineraryExplanationProvider>()
            .Should().BeOfType<HttpItineraryExplanationProvider>();
    }

    // C2
    [Fact]
    public void AddInfrastructure_BothFeaturesOnTripMateAi_ValidatesAndResolvesTripMateAiProviders()
    {
        var values = new Dictionary<string, string?>(TripMateAiRanking())
        {
            ["AiExplanation:TimeoutSeconds"] = "8",
            ["AiExplanation:OverallTimeoutSeconds"] = "9",
            ["AiExplanation:MaxAttempts"] = "1",
            ["AiExplanation:RetryBaseDelayMilliseconds"] = "250",
        };
        foreach (KeyValuePair<string, string?> value in TripMateAiExplanation())
        {
            values[value.Key] = value.Value;
        }

        using ServiceProvider provider = BuildProvider(BuildConfiguration(values));
        provider.GetRequiredService<IStartupValidator>().Validate();
        using IServiceScope scope = provider.CreateScope();

        PoiRankingProviderOptions ranking = provider.GetRequiredService<IOptions<PoiRankingProviderOptions>>().Value;
        ranking.Provider.Should().Be(AiProviderKind.TripMateAi);
        ranking.ModelName.Should().BeNull();
        provider.GetRequiredService<IOptions<ExplanationProviderOptions>>()
            .Value.Provider.Should().Be(AiProviderKind.TripMateAi);
        scope.ServiceProvider.GetRequiredService<IPoiRankingProvider>()
            .Should().BeOfType<TripMateAiPoiRankingProvider>();
        scope.ServiceProvider.GetRequiredService<IItineraryExplanationProvider>()
            .Should().BeOfType<TripMateAiExplanationProvider>();

        ItineraryExplanationExecutionOptions execution =
            provider.GetRequiredService<ItineraryExplanationExecutionOptions>();
        execution.Enabled.Should().BeTrue();
        execution.ProviderTimeout.Should().Be(TimeSpan.FromSeconds(8));
        execution.OverallTimeout.Should().Be(TimeSpan.FromSeconds(9));
        execution.MaxAttempts.Should().Be(1);
        execution.RetryBaseDelayMilliseconds.Should().Be(250);
    }

    // C3
    [Fact]
    public void AddInfrastructure_TripMateAiRankingWithGeminiExplanation_SelectsEachFeatureIndependently()
    {
        var values = new Dictionary<string, string?>(TripMateAiRanking());
        foreach (KeyValuePair<string, string?> value in GeminiExplanation())
        {
            values[value.Key] = value.Value;
        }

        using ServiceProvider provider = BuildProvider(BuildConfiguration(values));
        provider.GetRequiredService<IStartupValidator>().Validate();
        using IServiceScope scope = provider.CreateScope();

        scope.ServiceProvider.GetRequiredService<IPoiRankingProvider>()
            .Should().BeOfType<TripMateAiPoiRankingProvider>();
        scope.ServiceProvider.GetRequiredService<IItineraryExplanationProvider>()
            .Should().BeOfType<HttpItineraryExplanationProvider>();
    }

    // C3
    [Fact]
    public void AddInfrastructure_GeminiRankingWithTripMateAiExplanation_SelectsEachFeatureIndependently()
    {
        var values = new Dictionary<string, string?>(GeminiRanking());
        foreach (KeyValuePair<string, string?> value in TripMateAiExplanation())
        {
            values[value.Key] = value.Value;
        }

        using ServiceProvider provider = BuildProvider(BuildConfiguration(values));
        provider.GetRequiredService<IStartupValidator>().Validate();
        using IServiceScope scope = provider.CreateScope();

        scope.ServiceProvider.GetRequiredService<IPoiRankingProvider>()
            .Should().BeOfType<HttpPoiRankingProvider>();
        scope.ServiceProvider.GetRequiredService<IItineraryExplanationProvider>()
            .Should().BeOfType<TripMateAiExplanationProvider>();
    }

    // C4
    [Fact]
    public void AddInfrastructure_TripMateAiDisabledWithoutEndpointOrKey_PassesStartupValidation()
    {
        using ServiceProvider provider = BuildProvider(BuildConfiguration(new Dictionary<string, string?>
        {
            ["AiRanking:Enabled"] = "false",
            ["AiRanking:Provider"] = "TripMateAi",
            ["AiExplanation:Enabled"] = "false",
            ["AiExplanation:Provider"] = "TripMateAi",
        }));

        var startupAction = () => provider.GetRequiredService<IStartupValidator>().Validate();

        startupAction.Should().NotThrow();
        provider.GetRequiredService<IOptions<PoiRankingProviderOptions>>().Value.Endpoint.Should().BeNull();
        provider.GetRequiredService<IOptions<ExplanationProviderOptions>>().Value.ApiKey.Should().BeNull();
    }

    // C5
    [Theory]
    [InlineData("AiRanking:Provider")]
    [InlineData("AiExplanation:Provider")]
    public void AddInfrastructure_UnknownProviderName_Throws(string key)
    {
        IConfiguration configuration = BuildConfiguration(new Dictionary<string, string?>
        {
            [key] = "OpenAI",
        });
        var services = new ServiceCollection();

        var action = () => services.AddInfrastructure(configuration);

        action.Should().Throw<InvalidOperationException>();
    }

    // C6
    [Fact]
    public void StartupValidation_NumericProviderOutsideTheEnum_FailsWhenEnabled()
    {
        var values = new Dictionary<string, string?>(GeminiRanking())
        {
            ["AiRanking:Provider"] = "7",
        };
        foreach (KeyValuePair<string, string?> value in GeminiExplanation())
        {
            values[value.Key] = value.Value;
        }

        values["AiExplanation:Provider"] = "7";

        using ServiceProvider provider = BuildProvider(BuildConfiguration(values));

        var startupAction = () => provider.GetRequiredService<IStartupValidator>().Validate();

        startupAction.Should().Throw<AggregateException>()
            .Which.InnerExceptions.Should().NotBeEmpty()
            .And.AllBeOfType<OptionsValidationException>();
        var rankingAction = () => provider.GetRequiredService<IOptions<PoiRankingProviderOptions>>().Value;
        rankingAction.Should().Throw<OptionsValidationException>()
            .Which.Failures.Should().Contain("Provider must be Gemini or TripMateAi.");
        var explanationAction = () => provider.GetRequiredService<IOptions<ExplanationProviderOptions>>().Value;
        explanationAction.Should().Throw<OptionsValidationException>()
            .Which.Failures.Should().Contain("Provider must be Gemini or TripMateAi.");
    }

    // C6
    [Fact]
    public void StartupValidation_NumericProviderOutsideTheEnum_PassesWhenDisabledAndRegistersGemini()
    {
        using ServiceProvider provider = BuildProvider(BuildConfiguration(new Dictionary<string, string?>
        {
            ["AiRanking:Enabled"] = "false",
            ["AiRanking:Provider"] = "7",
            ["AiExplanation:Enabled"] = "false",
            ["AiExplanation:Provider"] = "7",
        }));

        provider.GetRequiredService<IStartupValidator>().Validate();
        using IServiceScope scope = provider.CreateScope();

        scope.ServiceProvider.GetRequiredService<IPoiRankingProvider>()
            .Should().BeOfType<HttpPoiRankingProvider>();
        scope.ServiceProvider.GetRequiredService<IItineraryExplanationProvider>()
            .Should().BeOfType<HttpItineraryExplanationProvider>();
    }

    // C7
    [Theory]
    [InlineData("https://ai.internal/v1/rank-pois", true)]
    [InlineData("http://ai:8000/v1/rank-pois", true)]
    [InlineData("http://tripmate-ai:8000/v1/rank-pois", true)]
    [InlineData("http://localhost:8000/v1/rank-pois", true)]
    [InlineData("http://127.0.0.1:8000/v1/rank-pois", true)]
    [InlineData("http://172.18.0.5:8000/v1/rank-pois", true)]
    [InlineData("http://ai.example.com/v1/rank-pois", false)]
    [InlineData("http://203.0.113.10:8000/v1/rank-pois", false)]
    [InlineData("/v1/rank-pois", false)]
    [InlineData("ftp://ai/v1/rank-pois", false)]
    [InlineData(null, false)]
    public void RankingValidator_TripMateAiEndpointScheme(string? endpoint, bool expectedValid)
    {
        var options = new PoiRankingProviderOptions
        {
            Enabled = true,
            Provider = AiProviderKind.TripMateAi,
            Endpoint = endpoint,
            ApiKey = SharedKey,
        };

        ValidateOptionsResult result = new PoiRankingProviderOptionsValidator().Validate(null, options);

        result.Succeeded.Should().Be(expectedValid);
        if (!expectedValid)
        {
            result.Failures.Should().ContainSingle().Which.Should().Be(
                "Endpoint must be an absolute HTTPS URI, or an HTTP URI on an internal host "
                + "(a Docker service name, localhost, or a private or link-local address), "
                + "when AI ranking uses TripMateAi.");
        }
    }

    // C7
    [Theory]
    [InlineData("https://ai.internal/v1/explain-itinerary", true)]
    [InlineData("http://ai:8000/v1/explain-itinerary", true)]
    [InlineData("http://tripmate-ai:8000/v1/explain-itinerary", true)]
    [InlineData("http://localhost:8000/v1/explain-itinerary", true)]
    [InlineData("http://127.0.0.1:8000/v1/explain-itinerary", true)]
    [InlineData("http://172.18.0.5:8000/v1/explain-itinerary", true)]
    [InlineData("http://ai.example.com/v1/explain-itinerary", false)]
    [InlineData("http://203.0.113.10:8000/v1/explain-itinerary", false)]
    [InlineData("/v1/explain-itinerary", false)]
    [InlineData("ftp://ai/v1/explain-itinerary", false)]
    [InlineData(null, false)]
    public void ExplanationValidator_TripMateAiEndpointScheme(string? endpoint, bool expectedValid)
    {
        var options = new ExplanationProviderOptions
        {
            Enabled = true,
            Provider = AiProviderKind.TripMateAi,
            Endpoint = endpoint,
            ApiKey = SharedKey,
        };

        ValidateOptionsResult result = new ExplanationProviderOptionsValidator().Validate(null, options);

        result.Succeeded.Should().Be(expectedValid);
        if (!expectedValid)
        {
            result.Failures.Should().ContainSingle().Which.Should().Be(
                "Endpoint must be an absolute HTTPS URI, or an HTTP URI on an internal host "
                + "(a Docker service name, localhost, or a private or link-local address), "
                + "when AI explanation uses TripMateAi.");
        }
    }

    // C7
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Validators_TripMateAiWithoutKey_Fail(string? apiKey)
    {
        ValidateOptionsResult ranking = new PoiRankingProviderOptionsValidator().Validate(
            null,
            new PoiRankingProviderOptions
            {
                Enabled = true,
                Provider = AiProviderKind.TripMateAi,
                Endpoint = RankingEndpoint,
                ApiKey = apiKey,
            });
        ValidateOptionsResult explanation = new ExplanationProviderOptionsValidator().Validate(
            null,
            new ExplanationProviderOptions
            {
                Enabled = true,
                Provider = AiProviderKind.TripMateAi,
                Endpoint = ExplanationEndpoint,
                ApiKey = apiKey,
            });

        ranking.Failures.Should().ContainSingle().Which.Should().Be(
            "ApiKey is required when AI ranking is enabled.");
        explanation.Failures.Should().ContainSingle().Which.Should().Be(
            "ApiKey is required when AI explanation is enabled.");
    }

    // C7
    [Theory]
    [InlineData(0, 2, "RateLimitPerMinute must be between 1 and 1000.")]
    [InlineData(1001, 2, "RateLimitPerMinute must be between 1 and 1000.")]
    [InlineData(4, 0, "MaxConcurrency must be between 1 and 100.")]
    [InlineData(4, 101, "MaxConcurrency must be between 1 and 100.")]
    public void Validators_TripMateAiBudgetOutOfRange_Fail(int rateLimit, int concurrency, string expected)
    {
        ValidateOptionsResult ranking = new PoiRankingProviderOptionsValidator().Validate(
            null,
            new PoiRankingProviderOptions
            {
                Enabled = true,
                Provider = AiProviderKind.TripMateAi,
                Endpoint = RankingEndpoint,
                ApiKey = SharedKey,
                RateLimitPerMinute = rateLimit,
                MaxConcurrency = concurrency,
            });
        ValidateOptionsResult explanation = new ExplanationProviderOptionsValidator().Validate(
            null,
            new ExplanationProviderOptions
            {
                Enabled = true,
                Provider = AiProviderKind.TripMateAi,
                Endpoint = ExplanationEndpoint,
                ApiKey = SharedKey,
                RateLimitPerMinute = rateLimit,
                MaxConcurrency = concurrency,
            });

        ranking.Failures.Should().ContainSingle().Which.Should().Be(expected);
        explanation.Failures.Should().ContainSingle().Which.Should().Be(expected);
    }

    // C7
    [Theory]
    [InlineData(0, 20, 2, 500, "TimeoutSeconds must be greater than zero.")]
    [InlineData(10, 31, 2, 500, "OverallTimeoutSeconds must be between 1 and 30 seconds.")]
    [InlineData(10, 9, 2, 500, "TimeoutSeconds cannot exceed OverallTimeoutSeconds.")]
    [InlineData(10, 20, 3, 500, "MaxAttempts must be 1 or 2.")]
    [InlineData(10, 20, 2, 5001, "RetryBaseDelayMilliseconds must be between 0 and 5000 milliseconds.")]
    public void ExplanationValidator_TripMateAiTimeoutsOutOfRange_Fail(
        int timeoutSeconds,
        int overallTimeoutSeconds,
        int maxAttempts,
        int retryDelayMilliseconds,
        string expected)
    {
        ValidateOptionsResult result = new ExplanationProviderOptionsValidator().Validate(
            null,
            new ExplanationProviderOptions
            {
                Enabled = true,
                Provider = AiProviderKind.TripMateAi,
                Endpoint = ExplanationEndpoint,
                ApiKey = SharedKey,
                TimeoutSeconds = timeoutSeconds,
                OverallTimeoutSeconds = overallTimeoutSeconds,
                MaxAttempts = maxAttempts,
                RetryBaseDelayMilliseconds = retryDelayMilliseconds,
            });

        result.Failures.Should().Contain(expected);
    }

    // C7
    [Fact]
    public void Validators_FailureMessages_NeverContainTheKey()
    {
        ValidateOptionsResult ranking = new PoiRankingProviderOptionsValidator().Validate(
            null,
            new PoiRankingProviderOptions
            {
                Enabled = true,
                Provider = AiProviderKind.TripMateAi,
                Endpoint = "ftp://ai/v1/rank-pois",
                ApiKey = SharedKey,
                RateLimitPerMinute = 0,
                MaxConcurrency = 0,
            });
        ValidateOptionsResult explanation = new ExplanationProviderOptionsValidator().Validate(
            null,
            new ExplanationProviderOptions
            {
                Enabled = true,
                Provider = AiProviderKind.TripMateAi,
                Endpoint = "ftp://ai/v1/explain-itinerary",
                ApiKey = SharedKey,
                TimeoutSeconds = 0,
                OverallTimeoutSeconds = 31,
                MaxAttempts = 3,
                RetryBaseDelayMilliseconds = -1,
                RateLimitPerMinute = 0,
                MaxConcurrency = 0,
            });

        ranking.Failed.Should().BeTrue();
        explanation.Failed.Should().BeTrue();
        ranking.Failures!.Concat(explanation.Failures!).Should().NotContain(message => message.Contains(SharedKey));
    }

    // C8
    [Fact]
    public void AddInfrastructure_LowercaseProviderName_SelectsTripMateAi()
    {
        var values = new Dictionary<string, string?>(TripMateAiRanking())
        {
            ["AiRanking:Provider"] = "tripmateai",
        };
        foreach (KeyValuePair<string, string?> value in TripMateAiExplanation())
        {
            values[value.Key] = value.Value;
        }

        values["AiExplanation:Provider"] = "tripmateai";

        using ServiceProvider provider = BuildProvider(BuildConfiguration(values));
        provider.GetRequiredService<IStartupValidator>().Validate();
        using IServiceScope scope = provider.CreateScope();

        provider.GetRequiredService<IOptions<PoiRankingProviderOptions>>()
            .Value.Provider.Should().Be(AiProviderKind.TripMateAi);
        scope.ServiceProvider.GetRequiredService<IPoiRankingProvider>()
            .Should().BeOfType<TripMateAiPoiRankingProvider>();
        scope.ServiceProvider.GetRequiredService<IItineraryExplanationProvider>()
            .Should().BeOfType<TripMateAiExplanationProvider>();
    }

    private static Dictionary<string, string?> TripMateAiRanking() => new()
    {
        ["AiRanking:Enabled"] = "true",
        ["AiRanking:Provider"] = "TripMateAi",
        ["AiRanking:Endpoint"] = RankingEndpoint,
        ["AiRanking:ApiKey"] = SharedKey,
    };

    private static Dictionary<string, string?> TripMateAiExplanation() => new()
    {
        ["AiExplanation:Enabled"] = "true",
        ["AiExplanation:Provider"] = "TripMateAi",
        ["AiExplanation:Endpoint"] = ExplanationEndpoint,
        ["AiExplanation:ApiKey"] = SharedKey,
    };

    private static Dictionary<string, string?> GeminiRanking() => new()
    {
        ["AiRanking:Enabled"] = "true",
        ["AiRanking:Provider"] = "Gemini",
        ["AiRanking:Endpoint"] = "https://provider.test/v1/interactions",
        ["AiRanking:ApiKey"] = "unit-test-provider-key",
        ["AiRanking:ModelName"] = "unit-test-model",
    };

    private static Dictionary<string, string?> GeminiExplanation() => new()
    {
        ["AiExplanation:Enabled"] = "true",
        ["AiExplanation:Provider"] = "Gemini",
        ["AiExplanation:Endpoint"] = "https://provider.test/v1/interactions",
        ["AiExplanation:ApiKey"] = "unit-test-provider-key",
        ["AiExplanation:ModelName"] = "unit-test-model",
    };

    private static ServiceProvider BuildProvider(IConfiguration configuration)
    {
        var services = new ServiceCollection();
        services.AddApplication();
        services.AddInfrastructure(configuration);
        return services.BuildServiceProvider();
    }

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
        yield return new("Cloudinary:OperatorDocumentsFolderRoot", "tripmate/tests/operator-documents");
        yield return new("SchedulingRateLimit:Provider", "SingleInstance");
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
}