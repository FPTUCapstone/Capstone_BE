using System.Text.Json;

using FluentAssertions;

using TripMate.Application.Features.Scheduling.Common;
using TripMate.Application.Features.Scheduling.Csp;

using Xunit;

namespace TripMate.Application.UnitTests.Features.Scheduling.Golden.Weighted;

public sealed record WeightedSummary(
    int TotalScenarios,
    int FeasibleScenarios,
    int CompletedSearchScenarios,
    IReadOnlyList<string> CompletedScenarioIds,
    IReadOnlyList<WeightedScenarioSummaryDetail> Details);

public sealed record WeightedScenarioSummaryDetail(
    string ScenarioId,
    string Source,
    bool Feasible,
    bool SearchCompleted,
    int NodesExpanded,
    long? BestModelObjective,
    long? BestEvaluatedObjective);

public class WeightedGoldenSnapshotTests
{
    private static readonly SchedulingGenerationOptions Options = new();
    private static readonly CspOptions GoldenCspOptions = new() { TimeLimitMilliseconds = 120_000 };

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
    };

    private static string GetSnapshotsDirectory()
    {
        var dir = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "../../../Features/Scheduling/Golden/Weighted/Snapshots"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    public static IEnumerable<object[]> GetScenarios() =>
        WeightedScenarioFactory.CreateAllScenarios().Select(s => new object[] { s });

    [Theory]
    [MemberData(nameof(GetScenarios))]
    public void Solver_MatchesWeightedGoldenSnapshot(GoldenScenario scenario)
    {
        ArgumentNullException.ThrowIfNull(scenario);

        var primaryCtx = CspItinerarySolver.CreatePrimaryContext(
            scenario.Input, scenario.Matrix, scenario.Candidates, scenario.Indices, Options);

        var requestInputs = WeightedGoldenInputsFactory.CreateRequestInputs(scenario.Candidates);
        var inputs = ObjectiveKernelFactory.BuildWeightedInputs(primaryCtx, requestInputs);
        var nodeOf = primaryCtx.Nodes.Select((n, i) => (n.Candidate.Id, Index: i)).ToDictionary(x => x.Id, x => x.Index);

        var baseCspOpts = scenario.CspOptions ?? GoldenCspOptions;
        var cspOptions = new CspOptions
        {
            Mode = ObjectiveMode.WeightedUtility,
            TimeLimitMilliseconds = baseCspOpts.TimeLimitMilliseconds,
            MaxNodes = baseCspOpts.MaxNodes,
            MaxStops = baseCspOpts.MaxStops,
            MaxOptionalDomainSize = baseCspOpts.MaxOptionalDomainSize,
            FinalCandidatesToValidate = baseCspOpts.FinalCandidatesToValidate,
            PolishWithLocalSearch = false
        };

        var solver = new CspItinerarySolver(new ItineraryScheduleEvaluator(Options), Options, cspOptions);
        var result = solver.Solve(
            scenario.Input,
            scenario.Matrix,
            scenario.Candidates,
            scenario.Indices,
            scenario.InitialIncumbent,
            weighted: requestInputs,
            cancellationToken: CancellationToken.None);

        var actualSnapshot = WeightedSnapshotMapper.From(scenario, result, primaryCtx, inputs, nodeOf);

        var snapshotDir = GetSnapshotsDirectory();
        var snapshotFile = Path.Combine(snapshotDir, $"{scenario.Id}.json");

        var isUpdateMode = Environment.GetEnvironmentVariable("TRIPMATE_WEIGHTED_GOLDEN_UPDATE") == "1";
        if (isUpdateMode)
        {
            var json = JsonSerializer.Serialize(actualSnapshot, JsonOptions);
            File.WriteAllText(snapshotFile, json);
            return;
        }

        if (!File.Exists(snapshotFile))
        {
            Assert.Fail($"Weighted snapshot file not found: {snapshotFile}. Run with TRIPMATE_WEIGHTED_GOLDEN_UPDATE=1 to generate.");
        }

        var expectedJson = File.ReadAllText(snapshotFile);
        var expectedSnapshot = JsonSerializer.Deserialize<WeightedSolverGoldenSnapshot>(expectedJson, JsonOptions);
        actualSnapshot.Should().BeEquivalentTo(expectedSnapshot);
    }

    [Fact]
    public void GenerateWeightedSummary_VerifiesCompletedScenariosThreshold()
    {
        var snapshotDir = GetSnapshotsDirectory();
        var scenarios = WeightedScenarioFactory.CreateAllScenarios();
        var details = new List<WeightedScenarioSummaryDetail>(scenarios.Count);

        foreach (var scenario in scenarios)
        {
            var snapshotFile = Path.Combine(snapshotDir, $"{scenario.Id}.json");
            WeightedSolverGoldenSnapshot snapshot;
            if (File.Exists(snapshotFile))
            {
                var json = File.ReadAllText(snapshotFile);
                snapshot = JsonSerializer.Deserialize<WeightedSolverGoldenSnapshot>(json, JsonOptions)!;
            }
            else
            {
                var primaryCtx = CspItinerarySolver.CreatePrimaryContext(
                    scenario.Input, scenario.Matrix, scenario.Candidates, scenario.Indices, Options);
                var requestInputs = WeightedGoldenInputsFactory.CreateRequestInputs(scenario.Candidates);
                var inputs = ObjectiveKernelFactory.BuildWeightedInputs(primaryCtx, requestInputs);
                var nodeOf = primaryCtx.Nodes.Select((n, i) => (n.Candidate.Id, Index: i)).ToDictionary(x => x.Id, x => x.Index);

                var baseCspOpts = scenario.CspOptions ?? GoldenCspOptions;
                var cspOptions = new CspOptions
                {
                    Mode = ObjectiveMode.WeightedUtility,
                    TimeLimitMilliseconds = baseCspOpts.TimeLimitMilliseconds,
                    MaxNodes = baseCspOpts.MaxNodes,
                    MaxStops = baseCspOpts.MaxStops,
                    MaxOptionalDomainSize = baseCspOpts.MaxOptionalDomainSize,
                    FinalCandidatesToValidate = baseCspOpts.FinalCandidatesToValidate,
                    PolishWithLocalSearch = false
                };

                var solver = new CspItinerarySolver(new ItineraryScheduleEvaluator(Options), Options, cspOptions);
                var result = solver.Solve(
                    scenario.Input,
                    scenario.Matrix,
                    scenario.Candidates,
                    scenario.Indices,
                    scenario.InitialIncumbent,
                    weighted: requestInputs,
                    cancellationToken: CancellationToken.None);
                snapshot = WeightedSnapshotMapper.From(scenario, result, primaryCtx, inputs, nodeOf);
            }


            details.Add(new WeightedScenarioSummaryDetail(
                snapshot.ScenarioId,
                snapshot.Source,
                snapshot.Feasible,
                snapshot.SearchCompleted,
                snapshot.NodesExpanded,
                snapshot.BestModelObjective,
                snapshot.BestEvaluatedObjective));
        }

        var completedIds = details.Where(d => d.SearchCompleted).Select(d => d.ScenarioId).ToList();
        var summary = new WeightedSummary(
            TotalScenarios: details.Count,
            FeasibleScenarios: details.Count(d => d.Feasible),
            CompletedSearchScenarios: completedIds.Count,
            CompletedScenarioIds: completedIds,
            Details: details);

        var summaryJson = JsonSerializer.Serialize(summary, JsonOptions);
        var summaryFile = Path.Combine(snapshotDir, "_weighted-summary.json");
        File.WriteAllText(summaryFile, summaryJson);

        completedIds.Count.Should().BeGreaterOrEqualTo(
            20,
            $"At least 20 scenarios must complete search fully (SearchCompleted = true) to validate pruning and optimality coverage. Actual: {completedIds.Count}");
    }
}