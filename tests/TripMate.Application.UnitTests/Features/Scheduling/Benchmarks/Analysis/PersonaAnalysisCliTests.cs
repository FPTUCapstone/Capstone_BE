using System.Net;
using System.Security.Cryptography;
using System.Text.Json;

using FluentAssertions;

using Microsoft.Extensions.Configuration;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Common.Models;
using TripMate.Application.Features.Scheduling.Personalization;
using TripMate.Infrastructure.Ai;
using TripMate.Infrastructure.AiRanking;
using TripMate.Infrastructure.TripMateAi;

using Xunit;

namespace TripMate.Application.UnitTests.Features.Scheduling.Benchmarks.Analysis;

public class PersonaAnalysisCliTests
{
    [Fact]
    public async Task RecordedMode_WhenCanonicalFixtureHashDoesNotMatch_IsBlockedBeforeExecutionOrWrite()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"tripmate-tampered-fixture-{Guid.NewGuid():N}");
        var fixturePath = Path.Combine(directory, "fixture.json");
        var artifactPath = Path.Combine(directory, "UC-10-persona-analysis-test.md");
        var error = new StringWriter();
        var output = new StringWriter();
        var artifactWritten = false;

        try
        {
            Directory.CreateDirectory(directory);
            var canonicalJson = await File.ReadAllTextAsync(BgeM3BenchmarkFixture.ResolveFixturePath());
            await File.WriteAllTextAsync(fixturePath, canonicalJson.Replace("0.82", "0.81", StringComparison.Ordinal));

            var exitCode = await PersonaAnalysisCommand.RunAsync(
                ["--mode", "recorded", "--output", artifactPath],
                output: output,
                error: error,
                artifactWriter: (path, markdown, cancellationToken) =>
                {
                    artifactWritten = true;
                    return Task.FromResult(path);
                },
                recordedFixturePath: fixturePath);

            exitCode.Should().Be(1);
            error.ToString().Should().Contain("BLOCKED").And.Contain("SHA-256");
            output.ToString().Should().NotContain("Executing Persona Analysis");
            artifactWritten.Should().BeFalse();
            File.Exists(artifactPath).Should().BeFalse();
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public async Task ConfigurationOverrides_AreUsedByRunnerReportAndFixture()
    {
        var values = ValidLiveConfiguration();
        values["Personalization:BaseWeight"] = "0.73";
        values["Personalization:AiWeight"] = "0.27";
        values["Personalization:PersonalizedAiFallbackScore"] = "0.61";
        values["Personalization:DefaultAiFallbackScore"] = "0.49";
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        var directory = Path.Combine(Path.GetTempPath(), $"tripmate-config-fixture-{Guid.NewGuid():N}");
        var fixturePath = Path.Combine(directory, "fixture.json");
        string? markdown = null;

        try
        {
            var exitCode = await PersonaAnalysisCommand.RunAsync(
                ["--mode", "live", "--output", "docs/benchmarks/UC-10-persona-analysis-test.md", "--fixture-output", fixturePath],
                configuration,
                output: new StringWriter(),
                error: new StringWriter(),
                providerFactory: (client, options) => new AlwaysSuccessfulProvider(),
                artifactWriter: (path, content, cancellationToken) =>
                {
                    markdown = content;
                    return Task.FromResult(path);
                });

            exitCode.Should().Be(0);
            markdown.Should().Contain("BaseWeight = 0.73, AiWeight = 0.27");
            markdown.Should().Contain("fallbackAiScore = 0.61 for personalized, 0.49 for default");

            var fixtureJson = await File.ReadAllTextAsync(fixturePath);
            using var fixture = JsonDocument.Parse(fixtureJson);
            var generalist = fixture.RootElement.GetProperty("Personas").EnumerateArray()
                .Single(persona => persona.GetProperty("PersonaId").GetString() == "DefaultGeneralist");
            generalist.GetProperty("Scores").EnumerateObject()
                .Select(score => score.Value.GetDecimal())
                .Should().OnlyContain(score => score == 0.49m);
            fixture.RootElement.GetProperty("Provenance").GetString().Should()
                .Contain("production cold-start fallback")
                .And.NotContain("0.50");
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public async Task InvalidPersonalizationConfiguration_IsBlockedBeforeExecution()
    {
        var values = ValidLiveConfiguration();
        values["Personalization:BaseWeight"] = "0.80";
        values["Personalization:AiWeight"] = "0.40";
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        var error = new StringWriter();
        var output = new StringWriter();
        var providerCreated = false;
        var artifactWritten = false;
        var fixtureWritten = false;

        var exitCode = await PersonaAnalysisCommand.RunAsync(
            ["--mode", "live", "--output", "docs/benchmarks/UC-10-persona-analysis-test.md", "--fixture-output", "fixtures/test.json"],
            configuration,
            output,
            error,
            providerFactory: (client, options) =>
            {
                providerCreated = true;
                return new AlwaysSuccessfulProvider();
            },
            artifactWriter: (path, markdown, cancellationToken) =>
            {
                artifactWritten = true;
                return Task.FromResult(path);
            },
            fixtureCapture: (snapshots, path, cancellationToken) =>
            {
                fixtureWritten = true;
                return Task.CompletedTask;
            });

        exitCode.Should().Be(1);
        error.ToString().Should().Contain("BLOCKED").And.Contain("personalization");
        output.ToString().Should().NotContain("Executing Persona Analysis");
        providerCreated.Should().BeFalse();
        artifactWritten.Should().BeFalse();
        fixtureWritten.Should().BeFalse();
    }

    [Fact]
    public async Task MalformedPersonalizationConfiguration_IsBlockedBeforeExecution()
    {
        var values = ValidLiveConfiguration();
        values["Personalization:BaseWeight"] = "not-a-decimal";
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        var error = new StringWriter();
        var output = new StringWriter();
        var providerCreated = false;
        var artifactWritten = false;
        var fixtureWritten = false;

        var exitCode = await PersonaAnalysisCommand.RunAsync(
            ["--mode", "live", "--output", "docs/benchmarks/UC-10-persona-analysis-test.md", "--fixture-output", "fixtures/test.json"],
            configuration,
            output,
            error,
            providerFactory: (client, options) =>
            {
                providerCreated = true;
                return new AlwaysSuccessfulProvider();
            },
            artifactWriter: (path, markdown, cancellationToken) =>
            {
                artifactWritten = true;
                return Task.FromResult(path);
            },
            fixtureCapture: (snapshots, path, cancellationToken) =>
            {
                fixtureWritten = true;
                return Task.CompletedTask;
            });

        exitCode.Should().Be(1);
        error.ToString().Should().Contain("BLOCKED").And.Contain("personalization");
        output.ToString().Should().NotContain("Executing Persona Analysis");
        providerCreated.Should().BeFalse();
        artifactWritten.Should().BeFalse();
        fixtureWritten.Should().BeFalse();
    }

    [Fact]
    public async Task RecordedMode_WithFixtureOutput_IsBlockedBeforeAnyExecutionOrWrite()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"tripmate-recorded-fixture-{Guid.NewGuid():N}");
        var artifactPath = Path.Combine(directory, "UC-10-persona-analysis-test.md");
        var fixturePath = Path.Combine(directory, "fixture.json");
        var error = new StringWriter();
        var output = new StringWriter();
        var providerCreated = false;
        var artifactWritten = false;
        var fixtureWritten = false;

        var exitCode = await PersonaAnalysisCommand.RunAsync(
            ["--mode", "recorded", "--output", artifactPath, "--fixture-output", fixturePath],
            output: output,
            error: error,
            providerFactory: (client, options) =>
            {
                providerCreated = true;
                return new AlwaysSuccessfulProvider();
            },
            artifactWriter: (path, markdown, cancellationToken) =>
            {
                artifactWritten = true;
                return Task.FromResult(path);
            },
            fixtureCapture: (snapshots, path, cancellationToken) =>
            {
                fixtureWritten = true;
                return Task.CompletedTask;
            });

        exitCode.Should().Be(1);
        error.ToString().Should().Contain("BLOCKED");
        output.ToString().Should().NotContain("Executing Persona Analysis");
        providerCreated.Should().BeFalse();
        artifactWritten.Should().BeFalse();
        fixtureWritten.Should().BeFalse();
        File.Exists(artifactPath).Should().BeFalse();
        File.Exists(fixturePath).Should().BeFalse();
        Directory.Exists(directory).Should().BeFalse();
    }

    [Theory]
    [InlineData("Gemini")]
    [InlineData("not-a-provider")]
    [InlineData("1")]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData(null)]
    public async Task PersonaAnalysisCommand_WhenLiveProviderIsNotTripMateAi_BlocksBeforeFactory(string? providerName)
    {
        var values = ValidLiveConfiguration();
        values["AiRanking:Provider"] = providerName;
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        var error = new StringWriter();
        var providerCreated = false;
        var artifactWritten = false;

        var exitCode = await PersonaAnalysisCommand.RunAsync(
            ["--mode", "live", "--output", "docs/benchmarks/UC-10-persona-analysis-test.md"],
            configuration,
            error: error,
            providerFactory: (client, options) =>
            {
                providerCreated = true;
                return new AlwaysSuccessfulProvider();
            },
            artifactWriter: (path, markdown, cancellationToken) =>
            {
                artifactWritten = true;
                return Task.FromResult(path);
            });

        exitCode.Should().Be(1);
        error.ToString().Should().Contain("BLOCKED").And.Contain("TripMateAi");
        providerCreated.Should().BeFalse();
        artifactWritten.Should().BeFalse();
    }

    [Theory]
    [InlineData("tripmateai")]
    [InlineData("TRIPMATEAI")]
    [InlineData("TripmateAi")]
    public async Task LiveMode_WhenProviderCasingIsNotExact_BlocksBeforeFactory(string providerName)
    {
        var values = ValidLiveConfiguration();
        values["AiRanking:Provider"] = providerName;
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        var error = new StringWriter();
        var providerCreated = false;

        var exitCode = await PersonaAnalysisCommand.RunAsync(
            ["--mode", "live", "--output", "docs/benchmarks/UC-10-persona-analysis-test.md"],
            configuration,
            output: new StringWriter(),
            error: error,
            providerFactory: (client, options) =>
            {
                providerCreated = true;
                return new AlwaysSuccessfulProvider();
            });

        exitCode.Should().Be(1);
        error.ToString().Should().Contain("BLOCKED").And.Contain("TripMateAi");
        providerCreated.Should().BeFalse();
    }

    [Fact]
    public async Task PersonaAnalysisCommand_WhenTripMateAiConfigurationIsValid_UsesFactoryAndDisposesOwnedClient()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(ValidLiveConfiguration()).Build();
        HttpClient? ownedClient = null;
        AiProviderKind? providerKind = null;

        var exitCode = await PersonaAnalysisCommand.RunAsync(
            ["--mode", "live", "--output", "docs/benchmarks/UC-10-persona-analysis-test.md"],
            configuration,
            output: new StringWriter(),
            error: new StringWriter(),
            providerFactory: (client, options) =>
            {
                ownedClient = client;
                providerKind = options.Provider;
                return new AlwaysSuccessfulProvider();
            },
            artifactWriter: (path, markdown, cancellationToken) => Task.FromResult(path),
            recordedFixturePath: "fixture-must-not-be-read-in-live-mode.json");

        exitCode.Should().Be(0);
        providerKind.Should().Be(AiProviderKind.TripMateAi);
        ownedClient.Should().NotBeNull();
        var send = () => ownedClient!.GetAsync("http://localhost", CancellationToken.None);
        await send.Should().ThrowAsync<ObjectDisposedException>();
    }

    [Fact]
    public async Task InjectedRankingOptions_RemainAuthoritative()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Personalization:BaseWeight"] = "0.55",
            ["Personalization:AiWeight"] = "0.45",
            ["Personalization:PersonalizedAiFallbackScore"] = "0.52",
            ["Personalization:DefaultAiFallbackScore"] = "0.48"
        }).Build();
        var rankingOptions = new PersonalizationRankingOptions
        {
            BaseWeight = 0.73m,
            AiWeight = 0.27m,
            PersonalizedAiFallbackScore = 0.61m,
            DefaultAiFallbackScore = 0.49m
        };
        string? markdown = null;

        var exitCode = await PersonaAnalysisCommand.RunAsync(
            ["--mode", "recorded", "--output", "docs/benchmarks/UC-10-persona-analysis-test.md"],
            configuration,
            output: new StringWriter(),
            error: new StringWriter(),
            artifactWriter: (path, content, cancellationToken) =>
            {
                markdown = content;
                return Task.FromResult(path);
            },
            rankingOptions: rankingOptions);

        exitCode.Should().Be(0);
        markdown.Should().NotBeNull();
        markdown.Should().Contain("BaseWeight = 0.73, AiWeight = 0.27");
        markdown.Should().Contain("0.73 * BaseScore + 0.27 * AiScore");
        markdown.Should().Contain("fallbackAiScore = 0.61 for personalized, 0.49 for default");
        markdown.Should().NotContain("0.60 * BaseScore + 0.40 * AiScore");
        markdown.Should().NotContain("fallbackAiScore = 0.60 for personalized, 0.50 for default");
        var actualFixtureHash = Convert.ToHexString(
            SHA256.HashData(await File.ReadAllBytesAsync(BgeM3BenchmarkFixture.ResolveFixturePath())));
        markdown.Should().Contain(actualFixtureHash);
    }

    [Fact]
    public async Task PersonaAnalysisCommand_WhenProbeAndAuthoritativeCallsSucceed_DoesNotCallProviderAgain()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(ValidLiveConfiguration()).Build();
        var provider = new ProbeAndOneCallPerPersonaProvider();
        var artifactWritten = false;
        var directory = Path.Combine(Path.GetTempPath(), $"tripmate-persona-fixture-{Guid.NewGuid():N}");
        var fixturePath = Path.Combine(directory, "fixture.json");
        try
        {
            var exitCode = await PersonaAnalysisCommand.RunAsync(
                ["--mode", "live", "--output", "docs/benchmarks/UC-10-persona-analysis-test.md", "--fixture-output", fixturePath],
                configuration,
                output: new StringWriter(),
                error: new StringWriter(),
                providerFactory: (client, options) => provider,
                artifactWriter: (path, markdown, cancellationToken) =>
                {
                    artifactWritten = true;
                    return Task.FromResult(path);
                });

            exitCode.Should().Be(0);
            artifactWritten.Should().BeTrue();
            File.Exists(fixturePath).Should().BeTrue();
            provider.CallCount.Should().Be(5, "fixture capture must reuse one probe plus four authoritative persona snapshots");
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public void Parse_SupportsRecordedAndLiveModes_WithOutput()
    {
        var recordedArgs = new[] { "--output", "docs/benchmarks/UC-10-persona-analysis-test.md" };
        var parsedRecorded = PersonaAnalysisCliParser.Parse(recordedArgs);
        parsedRecorded.Mode.Should().Be("recorded");
        parsedRecorded.OutputPath.Should().EndWith("UC-10-persona-analysis-test.md");
        parsedRecorded.FixtureOutputPath.Should().BeNull();

        var liveArgs = new[] { "--mode", "live", "--output", "docs/benchmarks/UC-10-persona-analysis-live.md", "--fixture-output", "fixtures/live.json" };
        var parsedLive = PersonaAnalysisCliParser.Parse(liveArgs);
        parsedLive.Mode.Should().Be("live");
        parsedLive.OutputPath.Should().EndWith("UC-10-persona-analysis-live.md");
        parsedLive.FixtureOutputPath.Should().Be("fixtures/live.json");
    }

    [Fact]
    public void Parse_ThrowsArgumentException_WhenModeIsInvalid()
    {
        var invalidArgs = new[] { "--mode", "unknown", "--output", "docs/benchmarks/UC-10-persona-analysis-test.md" };
        var act = () => PersonaAnalysisCliParser.Parse(invalidArgs);
        act.Should().Throw<ArgumentException>()
            .WithMessage("*Supported modes are 'live' and 'recorded'*");
    }

    [Fact]
    public async Task PersonaAnalysisCommand_WhenLiveMode_AndMissingEndpoint_ReturnsBlocked_WithNonZeroExitCode()
    {
        var inMemoryConfig = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["AiRanking:Provider"] = "TripMateAi",
                ["AiRanking:ApiKey"] = "valid-key"
                // Missing Endpoint
            })
            .Build();

        var errorWriter = new StringWriter();
        var outputWriter = new StringWriter();
        var writtenArtifact = false;

        var exitCode = await PersonaAnalysisCommand.RunAsync(
            ["--mode", "live", "--output", "docs/benchmarks/UC-10-persona-analysis-test.md"],
            configuration: inMemoryConfig,
            output: outputWriter,
            error: errorWriter,
            artifactWriter: (path, md, ct) =>
            {
                writtenArtifact = true;
                return Task.FromResult(path);
            });

        exitCode.Should().NotBe(0);
        errorWriter.ToString().Should().Contain("BLOCKED");
        errorWriter.ToString().Should().NotContain("valid-key", "API keys must never be logged");
        writtenArtifact.Should().BeFalse("no artifact must be written when BLOCKED");
    }

    [Fact]
    public async Task PersonaAnalysisCommand_WhenLiveMode_AndMissingApiKey_ReturnsBlocked_WithNonZeroExitCode()
    {
        var inMemoryConfig = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["AiRanking:Provider"] = "TripMateAi",
                ["AiRanking:Endpoint"] = "http://localhost:8000/v1/rank-pois"
                // Missing ApiKey
            })
            .Build();

        var errorWriter = new StringWriter();
        var outputWriter = new StringWriter();
        var writtenArtifact = false;

        var exitCode = await PersonaAnalysisCommand.RunAsync(
            ["--mode", "live", "--output", "docs/benchmarks/UC-10-persona-analysis-test.md"],
            configuration: inMemoryConfig,
            output: outputWriter,
            error: errorWriter,
            artifactWriter: (path, md, ct) =>
            {
                writtenArtifact = true;
                return Task.FromResult(path);
            });

        exitCode.Should().NotBe(0);
        errorWriter.ToString().Should().Contain("BLOCKED");
        writtenArtifact.Should().BeFalse();
    }

    [Fact]
    public async Task PersonaAnalysisCommand_WhenLiveMode_AndEndpointViolatesPolicy_FailsFastBeforeRequest()
    {
        var inMemoryConfig = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["AiRanking:Provider"] = "TripMateAi",
                ["AiRanking:Endpoint"] = "http://external-evil.com/v1/rank-pois", // External non-HTTPS HTTP violates policy
                ["AiRanking:ApiKey"] = "secret-key"
            })
            .Build();

        var errorWriter = new StringWriter();
        var outputWriter = new StringWriter();
        var requestMade = false;

        var exitCode = await PersonaAnalysisCommand.RunAsync(
            ["--mode", "live", "--output", "docs/benchmarks/UC-10-persona-analysis-test.md"],
            configuration: inMemoryConfig,
            output: outputWriter,
            error: errorWriter,
            providerFactory: (client, opts) =>
            {
                requestMade = true;
                return new TripMateAiPoiRankingProvider(client, Microsoft.Extensions.Options.Options.Create(opts));
            });

        exitCode.Should().NotBe(0);
        requestMade.Should().BeFalse("policy violation must fail before any HTTP request or provider instantiation");
        errorWriter.ToString().Should().Contain("BLOCKED");
    }

    [Fact]
    public async Task PersonaAnalysisCommand_WhenProbeSucceeds_ButPersonaRankingFails_AbortsAndCreatesNoArtifact()
    {
        var inMemoryConfig = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["AiRanking:Provider"] = "TripMateAi",
                ["AiRanking:Endpoint"] = "http://localhost:8000/v1/rank-pois",
                ["AiRanking:ApiKey"] = "secret-key"
            })
            .Build();

        var errorWriter = new StringWriter();
        var outputWriter = new StringWriter();
        var writtenArtifact = false;
        var capturedFixture = false;

        // Custom provider where probe succeeds, but second call (persona ranking) times out
        var mockProvider = new ProbeSuccessThenTimeoutProvider();

        var exitCode = await PersonaAnalysisCommand.RunAsync(
            ["--mode", "live", "--output", "docs/benchmarks/UC-10-persona-analysis-test.md", "--fixture-output", "fixtures/test.json"],
            configuration: inMemoryConfig,
            output: outputWriter,
            error: errorWriter,
            providerFactory: (client, opts) => mockProvider,
            artifactWriter: (path, md, ct) =>
            {
                writtenArtifact = true;
                return Task.FromResult(path);
            },
            fixtureCapture: (prov, path, ct) =>
            {
                capturedFixture = true;
                return Task.CompletedTask;
            });

        exitCode.Should().NotBe(0);
        errorWriter.ToString().Should().Contain("BLOCKED");
        writtenArtifact.Should().BeFalse("artifact must not be written if live persona ranking fails");
        capturedFixture.Should().BeFalse("fixture must not be captured if live persona ranking fails");
    }

    private sealed class ProbeSuccessThenTimeoutProvider : IPoiRankingProvider
    {
        private int _callCount;

        public Task<Result<PoiRankingResult>> RankAsync(PoiRankingRequest request, CancellationToken cancellationToken)
        {
            _callCount++;
            if (_callCount == 1)
            {
                // Probe call succeeds
                return Task.FromResult(Result.Success(new PoiRankingResult([new PoiRankingItem(1, 0.8m, "probe")])));
            }

            // Real persona ranking call fails (timeout or network)
            return Task.FromResult(Result.Failure<PoiRankingResult>(
                PoiRankingProviderErrorCodes.Network,
                "Simulated timeout/network during live evaluation"));
        }
    }

    [Fact]
    public async Task LiveProvider_WhenNetworkOrServiceUnavailable_ReturnsFailure_DoesNotSilentlyFallback()
    {
        using var httpClient = new HttpClient(new FailingNetworkHandler());
        var options = Microsoft.Extensions.Options.Options.Create(new PoiRankingProviderOptions
        {
            Enabled = true,
            Endpoint = "http://127.0.0.1:8000/v1/rank-pois",
            ApiKey = "test-key"
        });

        var liveProvider = new TripMateAiPoiRankingProvider(httpClient, options);

        var request = new PoiRankingRequest(
            [new PoiRankingCandidate(1L, "Test POI", "Culture", ["culture"])],
            new PoiRankingContext(["culture"]));

        var result = await liveProvider.RankAsync(request, CancellationToken.None);

        result.IsSuccess.Should().BeFalse("unreachable service must return failure, not pretend to succeed");
        result.ErrorCode.Should().Be(PoiRankingProviderErrorCodes.Network);
    }

    private sealed class FailingNetworkHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            throw new HttpRequestException("Simulated connection refused: Capstone_AI offline");
    }

    private sealed class AlwaysSuccessfulProvider : IPoiRankingProvider
    {
        public Task<Result<PoiRankingResult>> RankAsync(PoiRankingRequest request, CancellationToken cancellationToken) =>
            Task.FromResult(Result.Success(new PoiRankingResult(
                request.Candidates.Select(candidate => new PoiRankingItem(candidate.PoiId, 0.75m, "test")).ToArray())));
    }

    private sealed class ProbeAndOneCallPerPersonaProvider : IPoiRankingProvider
    {
        private readonly HashSet<string> _preferenceSets = new(StringComparer.Ordinal);

        public int CallCount { get; private set; }

        public Task<Result<PoiRankingResult>> RankAsync(PoiRankingRequest request, CancellationToken cancellationToken)
        {
            CallCount++;
            var key = string.Join('|', request.Context.PreferenceTokens.OrderBy(value => value, StringComparer.Ordinal));
            if (!_preferenceSets.Add(key))
            {
                return Task.FromResult(Result.Failure<PoiRankingResult>(
                    PoiRankingProviderErrorCodes.Network,
                    "unexpected repeated live provider call"));
            }

            return Task.FromResult(Result.Success(new PoiRankingResult(
                request.Candidates.Select(candidate => new PoiRankingItem(candidate.PoiId, 0.75m, "test")).ToArray())));
        }
    }

    private static Dictionary<string, string?> ValidLiveConfiguration() => new()
    {
        ["AiRanking:Provider"] = "TripMateAi",
        ["AiRanking:Endpoint"] = "http://localhost:8000/v1/rank-pois",
        ["AiRanking:ApiKey"] = "test-key",
        ["AiRanking:ModelName"] = "bge-m3:latest"
    };

    [Fact]
    public void LiveMode_UsesTripMateAiPoiRankingProvider_NotQwenOrExplanation()
    {
        var options = Microsoft.Extensions.Options.Options.Create(new PoiRankingProviderOptions
        {
            Enabled = true,
            Endpoint = "http://localhost:8000/v1/rank-pois",
            ApiKey = "test-key"
        });
        using var httpClient = new HttpClient();
        var liveProvider = new TripMateAiPoiRankingProvider(httpClient, options);

        liveProvider.Should().BeAssignableTo<IPoiRankingProvider>();
        liveProvider.GetType().Name.Should().Be(nameof(TripMateAiPoiRankingProvider));
        liveProvider.GetType().Name.Should().NotContain("Explanation");
        liveProvider.GetType().Name.Should().NotContain("Qwen");
    }
}