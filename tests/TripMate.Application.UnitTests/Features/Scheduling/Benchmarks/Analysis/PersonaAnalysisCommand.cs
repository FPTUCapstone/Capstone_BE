using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Common.Models;
using TripMate.Application.Features.Scheduling.Common;
using TripMate.Application.Features.Scheduling.Personalization;
using TripMate.Application.UnitTests.Features.Scheduling.Benchmarks.Shadow;
using TripMate.Infrastructure.Ai;
using TripMate.Infrastructure.AiRanking;
using TripMate.Infrastructure.TripMateAi;

namespace TripMate.Application.UnitTests.Features.Scheduling.Benchmarks.Analysis;

public static class PersonaAnalysisCommand
{
    public static async Task<int> RunAsync(
        string[] args,
        IConfiguration? configuration = null,
        TextWriter? output = null,
        TextWriter? error = null,
        Func<HttpClient, PoiRankingProviderOptions, IPoiRankingProvider>? providerFactory = null,
        Func<string, string, CancellationToken, Task<string>>? artifactWriter = null,
        Func<IReadOnlyDictionary<string, IReadOnlyDictionary<long, decimal>>, string, CancellationToken, Task>? fixtureCapture = null,
        PersonalizationRankingOptions? rankingOptions = null,
        string? recordedFixturePath = null,
        CancellationToken cancellationToken = default)
    {
        output ??= Console.Out;
        error ??= Console.Error;

        PersonaAnalysisCliOptions options;
        try
        {
            options = PersonaAnalysisCliParser.Parse(args);
        }
        catch (Exception ex)
        {
            await error.WriteLineAsync($"BLOCKED: CLI argument parsing failed: {ex.Message}");
            return 1;
        }

        try
        {
            configuration ??= new ConfigurationBuilder()
                .SetBasePath(AppContext.BaseDirectory)
                .AddJsonFile("appsettings.json", optional: true)
                .AddEnvironmentVariables()
                .Build();

            rankingOptions ??= configuration
                .GetSection(PersonalizationRankingOptions.SectionName)
                .Get<PersonalizationRankingOptions>()
                ?? new PersonalizationRankingOptions();
            var schedulingOptions = configuration
                .GetSection(SchedulingGenerationOptions.SectionName)
                .Get<SchedulingGenerationOptions>()
                ?? new SchedulingGenerationOptions();
            var rankingValidation = new PersonalizationRankingOptionsValidator(schedulingOptions)
                .Validate(null, rankingOptions);
            if (rankingValidation.Failed)
            {
                await error.WriteLineAsync($"BLOCKED: Invalid personalization configuration: {rankingValidation.FailureMessage}");
                return 1;
            }
        }
        catch (Exception)
        {
            await error.WriteLineAsync("BLOCKED: personalization configuration could not be loaded or validated.");
            return 1;
        }

        IPoiRankingProvider provider;
        HttpClient? httpClientToDispose = null;
        PoiRankingProviderOptions? providerOptions = null;
        string safeEndpoint;
        string fixtureSha256 = "N/A (Live service)";
        var cityData = SchedulingBenchmarkScenarioFactory.CreateDaNangPersonalizationData();

        if (options.Mode.Equals("live", StringComparison.OrdinalIgnoreCase))
        {
            var section = configuration.GetSection(PoiRankingProviderOptions.SectionName);
            var configuredProvider = section["Provider"];
            if (!Enum.TryParse(configuredProvider, ignoreCase: true, out AiProviderKind providerKind)
                || !string.Equals(configuredProvider, nameof(AiProviderKind.TripMateAi), StringComparison.Ordinal)
                || providerKind != AiProviderKind.TripMateAi)
            {
                await error.WriteLineAsync("BLOCKED: live BGE-M3 benchmark requires AiRanking:Provider == TripMateAi.");
                return 1;
            }

            providerOptions = new PoiRankingProviderOptions
            {
                Enabled = true,
                Provider = providerKind,
                Endpoint = section["Endpoint"],
                ApiKey = section["ApiKey"],
                ModelName = section["ModelName"] ?? "bge-m3:latest",
                RateLimitPerMinute = int.TryParse(section["RateLimitPerMinute"], out var r) ? r : 60,
                MaxConcurrency = int.TryParse(section["MaxConcurrency"], out var m) ? m : 4
            };

            var validator = new PoiRankingProviderOptionsValidator();
            var validationResult = validator.Validate(null, providerOptions);
            if (validationResult.Failed)
            {
                await error.WriteLineAsync($"BLOCKED: Invalid AI configuration: {validationResult.FailureMessage}");
                return 1;
            }

            if (Uri.TryCreate(providerOptions.Endpoint, UriKind.Absolute, out var uri))
            {
                safeEndpoint = $"{uri.Scheme}://{uri.Host}:{uri.Port}{uri.AbsolutePath}";
            }
            else
            {
                safeEndpoint = providerOptions.Endpoint ?? "unknown";
            }

            var probeCandidate = new PoiRankingCandidate(1L, "Probe POI", "Culture", ["culture"]);
            var probeRequest = new PoiRankingRequest([probeCandidate], new PoiRankingContext(["culture"]));

            try
            {
                httpClientToDispose = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
                provider = providerFactory != null
                    ? providerFactory(httpClientToDispose, providerOptions)
                    : new TripMateAiPoiRankingProvider(httpClientToDispose, Options.Create(providerOptions));

                using var probeCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                probeCts.CancelAfter(TimeSpan.FromSeconds(5));
                var probeResult = await provider.RankAsync(probeRequest, probeCts.Token);
                if (!probeResult.IsSuccess)
                {
                    await error.WriteLineAsync($"BLOCKED: live BGE-M3 service probe failed: {probeResult.ErrorCode} - {probeResult.ErrorMessage}");
                    httpClientToDispose.Dispose();
                    return 1;
                }
            }
            catch (Exception ex)
            {
                await error.WriteLineAsync($"BLOCKED: live BGE-M3 service probe exception: {ex.Message}");
                httpClientToDispose?.Dispose();
                return 1;
            }
        }
        else
        {
            try
            {
                var fixture = BgeM3BenchmarkFixture.LoadAndVerify(cityData, recordedFixturePath);
                safeEndpoint = "Recorded fixture (local deterministic)";
                fixtureSha256 = fixture.Sha256;
                provider = new RecordedPoiRankingProvider(fixture);
            }
            catch (Exception ex)
            {
                await error.WriteLineAsync($"BLOCKED: canonical BGE-M3 fixture verification failed: {ex.Message}");
                return 1;
            }
        }

        try
        {
            const double jaccardThreshold = 0.40;
            var recordingProvider = new RecordingPoiRankingProvider(provider);

            await output.WriteLineAsync($"Executing Persona Analysis (Mode: {options.Mode})...");

            var runResult = await PersonaAnalysisRunner.RunAsync(
                cityData,
                recordingProvider,
                rankingOptions,
                evaluationMode: options.Mode,
                jaccardThreshold: jaccardThreshold,
                cancellationToken: cancellationToken);

            if (!string.IsNullOrWhiteSpace(options.FixtureOutputPath))
            {
                if (fixtureCapture != null)
                {
                    await fixtureCapture(recordingProvider.Snapshots, options.FixtureOutputPath, cancellationToken);
                }
                else
                {
                    await CaptureFixtureAsync(
                        recordingProvider.Snapshots,
                        cityData,
                        providerOptions?.ModelName ?? BgeM3BenchmarkFixture.ModelName,
                        rankingOptions.DefaultAiFallbackScore,
                        options.FixtureOutputPath,
                        cancellationToken);
                }
                await output.WriteLineAsync($"Canonical fixture captured to: {options.FixtureOutputPath}");
            }

            var (gitCommit, gitState) = GetGitMetadata();
            var metadata = new PersonaAnalysisReportMetadata(
                RunDate: DateTimeOffset.UtcNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                BuildConfiguration: "Release",
                OperatingSystem: RuntimeInformation.OSDescription,
                Runtime: RuntimeInformation.FrameworkDescription,
                Machine: Environment.MachineName,
                LogicalProcessorCount: Environment.ProcessorCount,
                BaseWeight: rankingOptions.BaseWeight,
                AiWeight: rankingOptions.AiWeight,
                PersonalizedAiFallbackScore: rankingOptions.PersonalizedAiFallbackScore,
                DefaultAiFallbackScore: rankingOptions.DefaultAiFallbackScore,
                EvaluationMode: options.Mode,
                ModelName: options.Mode.Equals("live", StringComparison.OrdinalIgnoreCase)
                    ? (providerOptions?.ModelName ?? "bge-m3:latest")
                    : BgeM3BenchmarkFixture.ModelName,
                FixtureSha256: options.Mode.Equals("live", StringComparison.OrdinalIgnoreCase)
                    ? "N/A (Live service)"
                    : fixtureSha256,
                Endpoint: safeEndpoint,
                GitCommitHash: gitCommit,
                GitWorkingTreeState: gitState,
                SuccessCount: runResult.SuccessCount,
                ProviderSkippedCount: runResult.ProviderSkippedCount,
                FallbackCount: runResult.FallbackCount,
                JaccardThreshold: jaccardThreshold,
                CategoryAffinityWeight: rankingOptions.CategoryAffinityWeight,
                TagAffinityWeight: rankingOptions.TagAffinityWeight,
                BehaviorAffinityWeight: rankingOptions.BehaviorAffinityWeight,
                ScenicQualityWeight: rankingOptions.ScenicQualityWeight,
                PhotoQualityWeight: rankingOptions.PhotoQualityWeight);

            var markdown = PersonaAnalysisMarkdownRenderer.Render(metadata, runResult);

            string writtenPath;
            if (artifactWriter != null)
            {
                writtenPath = await artifactWriter(options.OutputPath, markdown, cancellationToken);
            }
            else
            {
                writtenPath = await PersonaAnalysisArtifactWriter.WriteAsync(options.OutputPath, markdown, cancellationToken);
            }

            await output.WriteLineAsync($"Artifact generated successfully at: {writtenPath} (Mode: {options.Mode})");
            return 0;
        }
        catch (Exception ex)
        {
            await error.WriteLineAsync($"BLOCKED: {ex.Message}");
            return 1;
        }
        finally
        {
            httpClientToDispose?.Dispose();
        }
    }

    private static async Task CaptureFixtureAsync(
        IReadOnlyDictionary<string, IReadOnlyDictionary<long, decimal>> snapshots,
        CityBenchmarkScenarioData cityData,
        string modelName,
        decimal defaultAiFallbackScore,
        string outputPath,
        CancellationToken cancellationToken)
    {
        var personaEntries = new List<BgeM3CanonicalPersonaEntry>(cityData.Personas.Count);
        foreach (var persona in cityData.Personas)
        {
            Dictionary<string, decimal> scores;
            if (persona.Tags.Count == 0)
            {
                scores = cityData.Pois.ToDictionary(p => p.Id.ToString(), _ => defaultAiFallbackScore);
            }
            else
            {
                if (!snapshots.TryGetValue(PreferenceKey(persona.Tags), out var snapshot))
                {
                    throw new InvalidOperationException(
                        $"Missing authoritative ranking snapshot for persona '{persona.Id}'.");
                }

                scores = snapshot.ToDictionary(pair => pair.Key.ToString(), pair => pair.Value);
            }

            personaEntries.Add(new BgeM3CanonicalPersonaEntry(persona.Id, persona.Tags, scores));
        }

        var document = new BgeM3CanonicalFixtureDocument(
            SchemaVersion: "1.0",
            CaptureTimestamp: DateTimeOffset.UtcNow,
            ScenarioId: cityData.Id,
            ModelName: modelName,
            EndpointMode: "live",
            Provenance: BgeM3BenchmarkFixture.Provenance,
            Personas: personaEntries);

        BgeM3CanonicalFixtureValidator.Validate(cityData, document);

        var json = JsonSerializer.Serialize(document, new JsonSerializerOptions
        {
            WriteIndented = true
        });

        var fullOutputPath = Path.GetFullPath(outputPath);
        var dir = Path.GetDirectoryName(fullOutputPath);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        var tempFile = Path.Combine(dir ?? ".", $".tmp-fixture-{Guid.NewGuid():N}.json");
        try
        {
            await File.WriteAllTextAsync(tempFile, json, cancellationToken);
            File.Move(tempFile, fullOutputPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(tempFile))
            {
                File.Delete(tempFile);
            }
        }
    }

    private static string PreferenceKey(IEnumerable<string> tokens) =>
        string.Join('|', tokens.OrderBy(value => value, StringComparer.OrdinalIgnoreCase));

    private sealed class RecordingPoiRankingProvider(IPoiRankingProvider inner) : IPoiRankingProvider
    {
        private readonly Dictionary<string, IReadOnlyDictionary<long, decimal>> _snapshots =
            new(StringComparer.OrdinalIgnoreCase);

        public IReadOnlyDictionary<string, IReadOnlyDictionary<long, decimal>> Snapshots => _snapshots;

        public async Task<Result<PoiRankingResult>> RankAsync(
            PoiRankingRequest request,
            CancellationToken cancellationToken)
        {
            var result = await inner.RankAsync(request, cancellationToken);
            if (result.IsSuccess)
            {
                _snapshots[PreferenceKey(request.Context.PreferenceTokens)] = result.Value.Ranked
                    .ToDictionary(item => item.PoiId, item => item.AiScore);
            }

            return result;
        }
    }

    private static (string CommitHash, string WorkingTreeState) GetGitMetadata()
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo("git", "rev-parse HEAD")
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var proc = System.Diagnostics.Process.Start(psi);
            var hash = proc?.StandardOutput.ReadToEnd().Trim() ?? "unknown";
            proc?.WaitForExit();

            var psiStatus = new System.Diagnostics.ProcessStartInfo("git", "status --porcelain")
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var procStatus = System.Diagnostics.Process.Start(psiStatus);
            var status = procStatus?.StandardOutput.ReadToEnd() ?? "";
            procStatus?.WaitForExit();

            var state = string.IsNullOrWhiteSpace(status) ? "clean" : "dirty";
            return (hash, state);
        }
        catch
        {
            return ("unknown", "unknown");
        }
    }
}