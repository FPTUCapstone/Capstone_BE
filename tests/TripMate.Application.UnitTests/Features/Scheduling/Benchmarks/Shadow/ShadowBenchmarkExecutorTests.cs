using FluentAssertions;

using TripMate.Application.Features.Scheduling.Common;
using TripMate.Application.Features.Scheduling.Csp;
using TripMate.Domain.Constants;
using TripMate.Domain.Enums;

namespace TripMate.Application.UnitTests.Features.Scheduling.Benchmarks.Shadow;

public class ShadowBenchmarkExecutorTests
{
    [Fact]
    public async Task RunScenarioAsync_AcceptsTwoCityDataInstancesThroughTheSamePipeline()
    {
        var persona = SchedulingBenchmarkScenarioFactory.CreateDaNangBaselineData().Personas[0];
        var first = SchedulingBenchmarkScenarioFactory.CreateScenario(CreateTinyCity("tiny-a", 101, 16.05m, persona));
        var second = SchedulingBenchmarkScenarioFactory.CreateScenario(CreateTinyCity("tiny-b", 202, 16.15m, persona));

        var results = await Task.WhenAll(new[] { first, second }.Select(scenario =>
            ShadowBenchmarkExecutor.RunScenarioAsync(
                scenario, persona, ObjectiveMode.Lexicographic, warmupIterations: 0, measuredIterations: 1)));

        results.Should().OnlyContain(result => result.Feasible);
        results.Select(result => result.ScenarioId).Should().Equal("tiny-a", "tiny-b");
        results.Should().OnlyContain(result => result.DurationSamplesMs.Count == 1);
    }

    [Fact]
    public async Task Baseline16_PreservesReviewedDeterministicOutcomesAcrossTheRefactor()
    {
        var data = SchedulingBenchmarkScenarioFactory.CreateDaNangBaselineData();
        var scenario = SchedulingBenchmarkScenarioFactory.CreateScenario(data);
        var persona = data.Personas.Single(value => value.Id == "CultureEnthusiast");

        var results = await Task.WhenAll(
            ShadowBenchmarkExecutor.RunScenarioAsync(
                scenario, persona, ObjectiveMode.Lexicographic, warmupIterations: 0, measuredIterations: 1),
            ShadowBenchmarkExecutor.RunScenarioAsync(
                scenario, persona, ObjectiveMode.WeightedUtility, warmupIterations: 0, measuredIterations: 1));
        var fingerprint = string.Join(";", results.Select(result =>
            $"{result.Mode}:{result.StopCount}:{result.TotalMatrixTravelMinutes}:{result.DiversityGroupCount}:"
            + $"{result.MiddayOutdoorVisitCount}:{result.ModelObjective?.ToString() ?? "-"}:"
            + string.Join(',', result.VisitedPoiIds)));

        fingerprint.Should().Be(
            "Lexicographic:5:58:1:0:-:1,5,11,2,3;WeightedUtility:6:31:1:0:26841:5,7,1,3,2,6");
    }

    [Fact]
    public void Render_DoesNotWriteAnArtifact()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"tripmate-shadow-{Guid.NewGuid():N}");
        var artifact = Path.Combine(directory, "shadow-local-2026-10-21-v3b.md");
        var metadata = new ShadowBenchmarkReportMetadata("2026-10-21", "Release", "test", ".NET", "test", 1, 1);

        _ = ShadowBenchmarkMarkdownRenderer.Render(metadata, []);

        File.Exists(artifact).Should().BeFalse();
        Directory.Exists(directory).Should().BeFalse();
    }

    [Fact]
    public void Render_GeneratesStructuredSummaryAndIsDeterministic()
    {
        var metadata = new ShadowBenchmarkReportMetadata("2026-10-21", "Release", "test-os", ".NET 10.0", "test-machine", 8, 1);
        var scenarios = new[] { "scenario-alpha", "scenario-beta" };
        var personas = new[] { "persona-1", "persona-2", "persona-3" };
        var modes = new[] { ObjectiveMode.Lexicographic, ObjectiveMode.WeightedUtility };

        var results = new List<ShadowBenchmarkRunResult>();
        foreach (var scenario in scenarios)
        {
            foreach (var persona in personas)
            {
                foreach (var mode in modes)
                {
                    var isFailed = scenario == "scenario-beta" && persona == "persona-3" && mode == ObjectiveMode.WeightedUtility;
                    results.Add(new ShadowBenchmarkRunResult(
                        ScenarioId: scenario,
                        PersonaId: persona,
                        Mode: mode,
                        Feasible: !isFailed,
                        StopCount: isFailed ? 0 : 4,
                        TotalMatrixTravelMinutes: isFailed ? 0 : 45,
                        DiversityGroupCount: isFailed ? 0 : 2,
                        MiddayOutdoorVisitCount: 0,
                        MiddayOutdoorVisitDenominator: isFailed ? 0 : 4,
                        MiddayOutdoorVisitRate: 0.0,
                        ModelObjective: isFailed ? null : 15000L,
                        VisitedPoiIds: isFailed ? [] : [101, 102],
                        DurationSamplesMs: [12.5],
                        P50Milliseconds: 12.5,
                        P95Milliseconds: 12.5));
                }
            }
        }

        results.Count.Should().Be(12);

        var markdown = ShadowBenchmarkMarkdownRenderer.Render(metadata, results);

        markdown.Should().Contain("Scenarios: 2");
        markdown.Should().Contain("Personas: 3");
        markdown.Should().Contain("Modes: 2");
        markdown.Should().Contain("Total cases: 12");
        markdown.Should().Contain("Failed cases: 1");

        var secondRender = ShadowBenchmarkMarkdownRenderer.Render(metadata, results);
        secondRender.Should().Be(markdown);
    }

    [Fact]
    public void ValidateV3b_RejectsTheExistingV3aArtifact()
    {
        var action = () => ShadowBenchmarkOutputPath.ValidateV3b(
            "docs/benchmarks/shadow-local-2026-10-19-v3a.md");

        action.Should().Throw<ArgumentException>();
        ShadowBenchmarkOutputPath.ValidateV3b("docs/benchmarks/shadow-local-2026-10-21-v3b.md")
            .Should().EndWith("shadow-local-2026-10-21-v3b.md");
    }

    [Fact]
    public async Task ExplicitWriter_CreatesOnlyTheRequestedV3bArtifact()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"tripmate-shadow-writer-{Guid.NewGuid():N}");
        var v3bPath = Path.Combine(directory, "shadow-local-2026-10-21-v3b.md");
        var v3aPath = Path.Combine(directory, "shadow-local-2026-10-19-v3a.md");
        try
        {
            var writtenPath = await ShadowBenchmarkArtifactWriter.WriteV3bAsync(v3bPath, "# benchmark\n");

            writtenPath.Should().Be(Path.GetFullPath(v3bPath));
            (await File.ReadAllTextAsync(v3bPath)).Should().Be("# benchmark\n");
            File.Exists(v3aPath).Should().BeFalse();
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    private static CityBenchmarkScenarioData CreateTinyCity(
        string id,
        long poiId,
        decimal latitude,
        BenchmarkPersonaData persona)
    {
        var hotel = new RoutePoint(latitude, 108.20m);
        return new CityBenchmarkScenarioData(
            id,
            "Synthetic city-as-data test fixture",
            true,
            new DateTimeOffset(2026, 10, 20, 1, 0, 0, TimeSpan.Zero),
            "Asia/Ho_Chi_Minh",
            hotel,
            hotel,
            120,
            TransportMode.Walking,
            RestPreference.None,
            100_000m,
            [poiId],
            [
                new BenchmarkPoiData(
                    poiId,
                    $"POI {poiId}",
                    new RoutePoint(latitude + 0.01m, 108.21m),
                    PoiCategoryNames.Culture,
                    30,
                    0m,
                    IndoorOutdoorType.Outdoor,
                    false,
                    7m,
                    7m,
                    ["culture"],
                    [new GenerationOpeningHours(2, new TimeOnly(7, 0), new TimeOnly(21, 0))])
            ],
            [persona]);
    }
}