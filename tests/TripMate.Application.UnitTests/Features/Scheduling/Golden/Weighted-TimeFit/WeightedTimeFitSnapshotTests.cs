using System.Text.Json;
using FluentAssertions;
using TripMate.Application.Features.Scheduling.Common;
using TripMate.Application.Features.Scheduling.Csp;
using TripMate.Application.Features.Scheduling.Csp.Objectives;
using TripMate.Application.Features.Scheduling.Routing;
using TripMate.Application.UnitTests.Features.Scheduling.Golden;
using TripMate.Application.UnitTests.Features.Scheduling.Golden.Weighted;
using TripMate.Domain.Enums;
using Xunit;

namespace TripMate.Application.UnitTests.Features.Scheduling.Golden.WeightedTimeFit;

public sealed record WeightedTimeFitSummary(
    int TotalScenarios,
    int FeasibleScenarios,
    int CompletedSearchScenarios,
    IReadOnlyList<string> CompletedScenarioIds,
    int ScenariosWithNonUnitBestFactor,
    IReadOnlyList<string> ScenariosWithNonUnitBestFactorIds,
    IReadOnlyList<WeightedTimeFitScenarioSummaryDetail> Details);

public sealed record WeightedTimeFitScenarioSummaryDetail(
    string ScenarioId,
    string Source,
    bool Feasible,
    bool SearchCompleted,
    int NodesExpanded,
    long? BestModelObjective,
    long? BestEvaluatedObjective,
    bool BestHasTimeFitFactorNotOne,
    IReadOnlyList<double>? BestVisitTimeFitFactors);

public class WeightedTimeFitSnapshotTests
{
    private static readonly SchedulingGenerationOptions Options = new();
    private static readonly CspOptions GoldenCspOptions = new() { TimeLimitMilliseconds = 120_000 };

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
    };

    public static string GetSnapshotsDirectory()
    {
        var dir = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "../../../Features/Scheduling/Golden/Weighted-TimeFit/Snapshots"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    public static IEnumerable<object[]> GetScenarios() =>
        WeightedScenarioFactory.CreateAllScenarios().Select(s => new object[] { s });

    [Theory]
    [MemberData(nameof(GetScenarios))]
    public void Solver_MatchesWeightedTimeFitGoldenSnapshot(GoldenScenario scenario)
    {
        ArgumentNullException.ThrowIfNull(scenario);

        var primaryCtx = CspItinerarySolver.CreatePrimaryContext(
            scenario.Input, scenario.Matrix, scenario.Candidates, scenario.Indices, Options);

        var requestInputs = WeightedTimeFitInputsFactory.CreateRequestInputs(scenario.Candidates);
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

        var isUpdateMode = Environment.GetEnvironmentVariable("TRIPMATE_WEIGHTED_TIMEFIT_UPDATE") == "1"
            || Environment.GetEnvironmentVariable("TRIPMATE_WEIGHTED_GOLDEN_UPDATE") == "1";

        if (isUpdateMode || !File.Exists(snapshotFile))
        {
            var json = JsonSerializer.Serialize(actualSnapshot, JsonOptions);
            File.WriteAllText(snapshotFile, json);
            return;
        }

        var expectedJson = File.ReadAllText(snapshotFile);
        var expectedSnapshot = JsonSerializer.Deserialize<WeightedSolverGoldenSnapshot>(expectedJson, JsonOptions);
        actualSnapshot.Should().BeEquivalentTo(expectedSnapshot);
    }

    [Fact]
    public void GenerateWeightedTimeFitSummary_VerifiesCompletedScenariosAndNonUnitFactors()
    {
        var snapshotDir = GetSnapshotsDirectory();
        var scenarios = WeightedScenarioFactory.CreateAllScenarios();
        var details = new List<WeightedTimeFitScenarioSummaryDetail>(scenarios.Count);

        foreach (var scenario in scenarios)
        {
            var primaryCtx = CspItinerarySolver.CreatePrimaryContext(
                scenario.Input, scenario.Matrix, scenario.Candidates, scenario.Indices, Options);
            var requestInputs = WeightedTimeFitInputsFactory.CreateRequestInputs(scenario.Candidates);
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

            var snapshot = WeightedSnapshotMapper.From(scenario, result, primaryCtx, inputs, nodeOf);

            // Ghi file snapshot nếu chưa tồn tại
            var snapshotFile = Path.Combine(snapshotDir, $"{scenario.Id}.json");
            if (!File.Exists(snapshotFile))
            {
                var json = JsonSerializer.Serialize(snapshot, JsonOptions);
                File.WriteAllText(snapshotFile, json);
            }

            // Tính toán hệ số TimeFit của lịch trình tốt nhất
            var (hasFactorNotOne, factors) = CalculateBestTimeFitFactors(primaryCtx, inputs, result, nodeOf);

            details.Add(new WeightedTimeFitScenarioSummaryDetail(
                snapshot.ScenarioId,
                snapshot.Source,
                snapshot.Feasible,
                snapshot.SearchCompleted,
                snapshot.NodesExpanded,
                snapshot.BestModelObjective,
                snapshot.BestEvaluatedObjective,
                hasFactorNotOne,
                factors));
        }

        var completedIds = details.Where(d => d.SearchCompleted).Select(d => d.ScenarioId).ToList();
        var nonUnitFactorIds = details.Where(d => d.BestHasTimeFitFactorNotOne).Select(d => d.ScenarioId).ToList();

        var summary = new WeightedTimeFitSummary(
            TotalScenarios: details.Count,
            FeasibleScenarios: details.Count(d => d.Feasible),
            CompletedSearchScenarios: completedIds.Count,
            CompletedScenarioIds: completedIds,
            ScenariosWithNonUnitBestFactor: nonUnitFactorIds.Count,
            ScenariosWithNonUnitBestFactorIds: nonUnitFactorIds,
            Details: details);

        var summaryJson = JsonSerializer.Serialize(summary, JsonOptions);
        var summaryFile = Path.Combine(snapshotDir, "_weighted-timefit-summary.json");
        File.WriteAllText(summaryFile, summaryJson);

        completedIds.Count.Should().BeGreaterOrEqualTo(
            20,
            $"At least 20 scenarios must complete search fully. Actual: {completedIds.Count}");

        nonUnitFactorIds.Count.Should().BeGreaterThan(
            0,
            $"At least one scenario must have TimeFit Factor of best different from 1.0. Actual: {nonUnitFactorIds.Count}");
    }

    private static (bool HasFactorNotOne, IReadOnlyList<double> Factors) CalculateBestTimeFitFactors(
        RoutingContext ctx,
        WeightedObjectiveInputs inputs,
        CspResult result,
        IReadOnlyDictionary<long, int> nodeOf)
    {
        if (result.Schedule is not { } schedule)
        {
            return (false, Array.Empty<double>());
        }

        var visitItems = schedule.Plan.Items
            .Where(i => i.Kind == ItineraryItemKind.Visit && i.PointOfInterestId.HasValue)
            .OrderBy(i => i.SequenceNo)
            .ToList();

        var factors = new List<double>(visitItems.Count);
        var hasFactorNotOne = false;

        foreach (var item in visitItems)
        {
            if (nodeOf.TryGetValue(item.PointOfInterestId!.Value, out var node))
            {
                var start = ObjectiveCalculator.ToModelMinute(item.PlannedArrivalUtc, ctx.Input.StartAtUtc);
                var end = ObjectiveCalculator.ToModelMinute(item.PlannedDepartureUtc, ctx.Input.StartAtUtc);
                var factor = inputs.TimeFit.Factor(node, start, end);
                factors.Add(Math.Round(factor, 4));
                if (Math.Abs(factor - 1.0) > 1e-4)
                {
                    hasFactorNotOne = true;
                }
            }
        }

        return (hasFactorNotOne, factors);
    }
}
