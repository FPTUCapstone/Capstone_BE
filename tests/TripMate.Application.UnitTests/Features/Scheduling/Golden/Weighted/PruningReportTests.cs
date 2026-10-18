using System.Text;
using System.Text.Json;
using FluentAssertions;
using TripMate.Application.Features.Scheduling.Common;
using TripMate.Application.Features.Scheduling.Csp;
using TripMate.Application.Features.Scheduling.Csp.Objectives;
using TripMate.Application.UnitTests.Features.Scheduling.Fixtures;
using Xunit;

namespace TripMate.Application.UnitTests.Features.Scheduling.Golden.Weighted;

public sealed record PruningReport(
    string GeneratedAtUtc,
    IReadOnlyList<PruningInstanceDetail> Instances,
    double AverageReductionPercent,
    GoldenPruningComparison GoldenComparison);

public sealed record PruningInstanceDetail(
    string InstanceId,
    int Candidates,
    int Mandatory,
    int MaxNodes,
    int NodesWithoutBound,
    int NodesWithBound,
    int PrunedByBound,
    double ReductionPercent,
    bool CompletedWithoutBound,
    bool CompletedWithBound,
    long? ObjectiveWithoutBound,
    long? ObjectiveWithBound);

public sealed record GoldenPruningComparison(
    int TotalScenarios,
    int ShiftedToSearchCompletedCount,
    IReadOnlyList<string> ShiftedScenarioIds,
    int BestObjectiveIncreasedCount,
    double AverageObjectiveIncrease,
    int BaselineCompletedCount,
    int BaselineCompletedEliteMatchesCount,
    bool AllBaselineCompletedEliteMatch,
    IReadOnlyList<GoldenComparisonItem> Details);

public sealed record GoldenComparisonItem(
    string ScenarioId,
    bool BaselineNodeLimitReached,
    bool BaselineSearchCompleted,
    bool CurrentSearchCompleted,
    bool Shifted,
    long? BaselineBestModelObjective,
    long? CurrentBestModelObjective,
    bool ObjectiveIncreased,
    long? ObjectiveDiff,
    bool FirstEliteMatches);

public class PruningReportTests
{
    private static readonly SchedulingGenerationOptions Options = new();

    private static string GetWeightedDirectory() =>
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../Features/Scheduling/Golden/Weighted"));

    private static (IReadOnlyList<GenerationCandidate> Candidates, IReadOnlyDictionary<long, int> Indices) Index(GenerationInput input)
    {
        var candidates = input.Candidates.OrderBy(c => c.Id).ToList();
        var indices = candidates.Select((c, i) => (c.Id, Index: i + 1)).ToDictionary(x => x.Id, x => x.Index);
        return (candidates, indices);
    }

    [Fact]
    public void GeneratePruningReport_ProducesValidReport_AndSatisfiesAllInvariants()
    {
        var weightedDir = GetWeightedDirectory();
        var baselineDir = Path.Combine(weightedDir, "Baseline-PR06");
        var snapshotsDir = Path.Combine(weightedDir, "Snapshots");

        // -------------------------------------------------------------
        // Part a: 8 instances n=12..15
        // -------------------------------------------------------------
        (int n, int m, int seed, int availableMin, int? maxStops, int maxNodes)[] configs =
        [
            (12, 0, 81, 270, 5, 500_000),
            (12, 1, 82, 270, 5, 500_000),
            (13, 0, 83, 270, 5, 500_000),
            (13, 1, 84, 270, 5, 500_000),
            (14, 0, 85, 270, 5, 500_000),
            (14, 1, 86, 270, 5, 500_000),
            (15, 0, 87, 270, 5, 500_000),
            (15, 1, 88, 270, 5, 500_000)
        ];

        var instanceDetails = new List<PruningInstanceDetail>();

        foreach (var (n, m, seed, availableMin, maxStops, maxNodes) in configs)
        {
            var id = $"instance-n{n}-m{m}-s{seed}";
            var (rawInput, matrix) = OptionalRouteOptimizationScenarios.CreateSyntheticCorpusScenario(
                n, m, seed, TripMate.Domain.Enums.RestPreference.None);
            var input = rawInput with { AvailableMinutes = availableMin };
            var (cands, idx) = Index(input);

            var reqInputs = WeightedGoldenInputsFactory.CreateRequestInputs(cands);
            var primaryCtx = CspItinerarySolver.CreatePrimaryContext(input, matrix, cands, idx, Options);
            var weightedInputs = ObjectiveKernelFactory.BuildWeightedInputs(primaryCtx, reqInputs);
            var nodeOf = primaryCtx.Nodes.Select((node, i) => (node.Candidate.Id, Index: i)).ToDictionary(x => x.Id, x => x.Index);

            // Run WITHOUT bound
            var cspOptsNoBound = new CspOptions
            {
                Mode = ObjectiveMode.WeightedUtility,
                DisableBound = true,
                MaxNodes = maxNodes,
                MaxStops = maxStops,
                TimeLimitMilliseconds = 60_000,
                PolishWithLocalSearch = false
            };
            var solverNoBound = new CspItinerarySolver(new ItineraryScheduleEvaluator(Options), Options, cspOptsNoBound);
            var resultNoBound = solverNoBound.Solve(input, matrix, cands, idx, null, reqInputs, CancellationToken.None);
            var snapNoBound = WeightedSnapshotMapper.From(new GoldenScenario(id, "Instance", true, input, matrix, cands, idx), resultNoBound, primaryCtx, weightedInputs, nodeOf);

            // Run WITH bound
            var cspOptsWithBound = new CspOptions
            {
                Mode = ObjectiveMode.WeightedUtility,
                DisableBound = false,
                BoundVariant = BoundVariant.Correct,
                MaxNodes = maxNodes,
                MaxStops = maxStops,
                TimeLimitMilliseconds = 60_000,
                PolishWithLocalSearch = false
            };
            var solverWithBound = new CspItinerarySolver(new ItineraryScheduleEvaluator(Options), Options, cspOptsWithBound);
            var resultWithBound = solverWithBound.Solve(input, matrix, cands, idx, null, reqInputs, CancellationToken.None);
            var snapWithBound = WeightedSnapshotMapper.From(new GoldenScenario(id, "Instance", true, input, matrix, cands, idx), resultWithBound, primaryCtx, weightedInputs, nodeOf);

            var nodesNo = snapNoBound.NodesExpanded;
            var nodesWith = snapWithBound.NodesExpanded;
            var reduction = nodesNo > 0 ? (1.0 - (double)nodesWith / nodesNo) * 100.0 : 0.0;

            instanceDetails.Add(new PruningInstanceDetail(
                InstanceId: id,
                Candidates: n,
                Mandatory: m,
                MaxNodes: maxNodes,
                NodesWithoutBound: nodesNo,
                NodesWithBound: nodesWith,
                PrunedByBound: snapWithBound.PrunedByBound,
                ReductionPercent: Math.Round(reduction, 2),
                CompletedWithoutBound: snapNoBound.SearchCompleted,
                CompletedWithBound: snapWithBound.SearchCompleted,
                ObjectiveWithoutBound: snapNoBound.BestModelObjective,
                ObjectiveWithBound: snapWithBound.BestModelObjective));
        }

        var avgReduction = Math.Round(instanceDetails.Average(i => i.ReductionPercent), 2);

        // -------------------------------------------------------------
        // Part b: 67 Weighted Golden Scenarios vs Baseline-PR06
        // -------------------------------------------------------------
        var allScenarios = WeightedScenarioFactory.CreateAllScenarios();
        var goldenItems = new List<GoldenComparisonItem>();

        var shiftedIds = new List<string>();
        var objectiveIncreases = new List<long>();
        var baselineCompletedTotal = 0;
        var baselineCompletedEliteMatches = 0;

        foreach (var scenario in allScenarios)
        {
            var baseFile = Path.Combine(baselineDir, $"{scenario.Id}.json");
            var currentFile = Path.Combine(snapshotsDir, $"{scenario.Id}.json");

            if (!File.Exists(baseFile) || !File.Exists(currentFile))
            {
                continue;
            }

            var baseSnap = JsonSerializer.Deserialize<WeightedSolverGoldenSnapshot>(File.ReadAllText(baseFile))!;
            var currSnap = JsonSerializer.Deserialize<WeightedSolverGoldenSnapshot>(File.ReadAllText(currentFile))!;

            var shifted = baseSnap.NodeLimitReached && currSnap.SearchCompleted;
            if (shifted)
            {
                shiftedIds.Add(scenario.Id);
            }

            var objDiff = (currSnap.BestModelObjective.HasValue && baseSnap.BestModelObjective.HasValue)
                ? currSnap.BestModelObjective.Value - baseSnap.BestModelObjective.Value
                : (long?)null;

            var objIncreased = objDiff.HasValue && objDiff.Value > 0;
            if (objIncreased)
            {
                objectiveIncreases.Add(objDiff!.Value);
            }

            var baseFirst = baseSnap.EliteVisitIds.FirstOrDefault();
            var currFirst = currSnap.EliteVisitIds.FirstOrDefault();
            var firstEliteMatches = (baseFirst == null && currFirst == null)
                || (baseFirst != null && currFirst != null && baseFirst.SequenceEqual(currFirst));

            if (baseSnap.SearchCompleted)
            {
                baselineCompletedTotal++;
                if (firstEliteMatches)
                {
                    baselineCompletedEliteMatches++;
                }
            }

            goldenItems.Add(new GoldenComparisonItem(
                ScenarioId: scenario.Id,
                BaselineNodeLimitReached: baseSnap.NodeLimitReached,
                BaselineSearchCompleted: baseSnap.SearchCompleted,
                CurrentSearchCompleted: currSnap.SearchCompleted,
                Shifted: shifted,
                BaselineBestModelObjective: baseSnap.BestModelObjective,
                CurrentBestModelObjective: currSnap.BestModelObjective,
                ObjectiveIncreased: objIncreased,
                ObjectiveDiff: objDiff,
                FirstEliteMatches: firstEliteMatches));
        }

        var avgObjIncrease = objectiveIncreases.Count > 0 ? Math.Round(objectiveIncreases.Average(), 2) : 0.0;
        var allBaselineCompletedMatch = baselineCompletedTotal > 0 && baselineCompletedEliteMatches == baselineCompletedTotal;

        var goldenComparison = new GoldenPruningComparison(
            TotalScenarios: goldenItems.Count,
            ShiftedToSearchCompletedCount: shiftedIds.Count,
            ShiftedScenarioIds: shiftedIds,
            BestObjectiveIncreasedCount: objectiveIncreases.Count,
            AverageObjectiveIncrease: avgObjIncrease,
            BaselineCompletedCount: baselineCompletedTotal,
            BaselineCompletedEliteMatchesCount: baselineCompletedEliteMatches,
            AllBaselineCompletedEliteMatch: allBaselineCompletedMatch,
            Details: goldenItems);

        var report = new PruningReport(
            GeneratedAtUtc: DateTimeOffset.UtcNow.ToString("O"),
            Instances: instanceDetails,
            AverageReductionPercent: avgReduction,
            GoldenComparison: goldenComparison);

        var jsonOpts = new JsonSerializerOptions { WriteIndented = true };
        var jsonText = JsonSerializer.Serialize(report, jsonOpts);
        var lfText = jsonText.Replace("\r\n", "\n") + "\n";

        var reportPath = Path.Combine(weightedDir, "_pruning-report.json");
        File.WriteAllBytes(reportPath, Encoding.UTF8.GetBytes(lfText));

        // -------------------------------------------------------------
        // Assertions
        // -------------------------------------------------------------
        instanceDetails.Should().HaveCount(8);
        instanceDetails.Should().OnlyContain(i => i.CompletedWithoutBound && i.CompletedWithBound);
        instanceDetails.Should().OnlyContain(i => i.ObjectiveWithBound == i.ObjectiveWithoutBound);
        avgReduction.Should().BeGreaterThan(0.0);

        allBaselineCompletedMatch.Should().BeTrue(
            $"All {baselineCompletedTotal} baseline completed scenarios must preserve identical first Elite solution.");
    }
}
