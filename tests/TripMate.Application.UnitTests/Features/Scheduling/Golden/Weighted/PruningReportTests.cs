using System.Diagnostics;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using TripMate.Application.Features.Scheduling.Common;
using TripMate.Application.Features.Scheduling.Csp;
using TripMate.Application.Features.Scheduling.Csp.Objectives;
using TripMate.Application.UnitTests.Features.Scheduling.Fixtures;
using Xunit;
using Xunit.Abstractions;

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
    private readonly ITestOutputHelper _output;
    private static readonly SchedulingGenerationOptions Options = new();

    public PruningReportTests(ITestOutputHelper output)
    {
        _output = output;
    }

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

        _output.WriteLine("=== BẢNG TỶ LỆ GIẢM NODE TRÊN 8 INSTANCE n=12..15 ===");
        _output.WriteLine("| Instance | n | m | Không cận | PR-07 | G1 | G1+G2 | Giảm vs không cận | Giảm vs PR-07 | Giảm vs G1 |");
        _output.WriteLine("| :--- | :---: | :---: | :---: | :---: | :---: | :---: | :---: | :---: | :---: |");

        long totalNo = 0, totalPR07 = 0, totalG1 = 0, totalG1G2 = 0;

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

            // 1. Run WITHOUT bound
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

            // 2. Run PR-07 bound (IgnoreOptionalIncomingTravel)
            var cspOptsPR07 = new CspOptions
            {
                Mode = ObjectiveMode.WeightedUtility,
                DisableBound = false,
                BoundVariant = BoundVariant.IgnoreOptionalIncomingTravel,
                MaxNodes = maxNodes,
                MaxStops = maxStops,
                TimeLimitMilliseconds = 60_000,
                PolishWithLocalSearch = false
            };
            var solverPR07 = new CspItinerarySolver(new ItineraryScheduleEvaluator(Options), Options, cspOptsPR07);
            var resultPR07 = solverPR07.Solve(input, matrix, cands, idx, null, reqInputs, CancellationToken.None);

            // 3. Run G1 bound (StaticMaxFactor)
            var cspOptsG1 = new CspOptions
            {
                Mode = ObjectiveMode.WeightedUtility,
                DisableBound = false,
                BoundVariant = BoundVariant.StaticMaxFactor,
                MaxNodes = maxNodes,
                MaxStops = maxStops,
                TimeLimitMilliseconds = 60_000,
                PolishWithLocalSearch = false
            };
            var solverG1 = new CspItinerarySolver(new ItineraryScheduleEvaluator(Options), Options, cspOptsG1);
            var resultG1 = solverG1.Solve(input, matrix, cands, idx, null, reqInputs, CancellationToken.None);

            // 4. Run G1+G2 bound (Correct)
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
            var nodesPR07 = resultPR07.Statistics.NodesExpanded;
            var nodesG1 = resultG1.Statistics.NodesExpanded;
            var nodesWith = snapWithBound.NodesExpanded;

            totalNo += nodesNo;
            totalPR07 += nodesPR07;
            totalG1 += nodesG1;
            totalG1G2 += nodesWith;

            var redVsNo = nodesNo > 0 ? (1.0 - (double)nodesWith / nodesNo) * 100.0 : 0.0;
            var redVsPR07 = nodesPR07 > 0 ? (1.0 - (double)nodesWith / nodesPR07) * 100.0 : 0.0;
            var redVsG1 = nodesG1 > 0 ? (1.0 - (double)nodesWith / nodesG1) * 100.0 : 0.0;

            _output.WriteLine($"| `{id}` | {n} | {m} | {nodesNo:N0} | {nodesPR07:N0} | {nodesG1:N0} | {nodesWith:N0} | {redVsNo:F2}% | {redVsPR07:F2}% | {redVsG1:F2}% |");

            instanceDetails.Add(new PruningInstanceDetail(
                InstanceId: id,
                Candidates: n,
                Mandatory: m,
                MaxNodes: maxNodes,
                NodesWithoutBound: nodesNo,
                NodesWithBound: nodesWith,
                PrunedByBound: snapWithBound.PrunedByBound,
                ReductionPercent: Math.Round(redVsNo, 2),
                CompletedWithoutBound: snapNoBound.SearchCompleted,
                CompletedWithBound: snapWithBound.SearchCompleted,
                ObjectiveWithoutBound: snapNoBound.BestModelObjective,
                ObjectiveWithBound: snapWithBound.BestModelObjective));
        }

        var totalRedVsNo = totalNo > 0 ? (1.0 - (double)totalG1G2 / totalNo) * 100.0 : 0.0;
        var totalRedVsPR07 = totalPR07 > 0 ? (1.0 - (double)totalG1G2 / totalPR07) * 100.0 : 0.0;
        var totalRedVsG1 = totalG1 > 0 ? (1.0 - (double)totalG1G2 / totalG1) * 100.0 : 0.0;

        _output.WriteLine($"| **Tổng cộng** | - | - | **{totalNo:N0}** | **{totalPR07:N0}** | **{totalG1:N0}** | **{totalG1G2:N0}** | **{totalRedVsNo:F2}%** | **{totalRedVsPR07:F2}%** | **{totalRedVsG1:F2}%** |");

        // -------------------------------------------------------------
        // Benchmark ns/node: Weighted vs Lexicographic (n=25)
        // -------------------------------------------------------------
        var (benchInput, benchMatrix) = OptionalRouteOptimizationScenarios.CreateSyntheticCorpusScenario(
            candidateCount: 25,
            mandatoryCount: 2,
            seed: 101,
            restPreference: TripMate.Domain.Enums.RestPreference.None);
        var (benchCands, benchIdx) = Index(benchInput);
        var benchReqInputs = WeightedGoldenInputsFactory.CreateRequestInputs(benchCands);

        var lexOptions = new CspOptions
        {
            Mode = ObjectiveMode.Lexicographic,
            MaxNodes = 50_000,
            TimeLimitMilliseconds = 60_000,
            PolishWithLocalSearch = false
        };
        var weightOptions = new CspOptions
        {
            Mode = ObjectiveMode.WeightedUtility,
            MaxNodes = 50_000,
            TimeLimitMilliseconds = 60_000,
            PolishWithLocalSearch = false
        };

        var lexSolver = new CspItinerarySolver(new ItineraryScheduleEvaluator(Options), Options, lexOptions);
        var weightSolver = new CspItinerarySolver(new ItineraryScheduleEvaluator(Options), Options, weightOptions);

        // Warmup
        lexSolver.Solve(benchInput, benchMatrix, benchCands, benchIdx, null);
        weightSolver.Solve(benchInput, benchMatrix, benchCands, benchIdx, null, benchReqInputs);

        const int benchIterations = 5;
        var swLex = Stopwatch.StartNew();
        long lexTotalNodes = 0;
        for (var i = 0; i < benchIterations; i++)
        {
            var r = lexSolver.Solve(benchInput, benchMatrix, benchCands, benchIdx, null);
            lexTotalNodes += r.Statistics.NodesExpanded;
        }
        swLex.Stop();
        var lexNsPerNode = lexTotalNodes > 0 ? (double)swLex.Elapsed.Ticks * 100 / lexTotalNodes : 0;

        var swWeight = Stopwatch.StartNew();
        long weightTotalNodes = 0;
        for (var i = 0; i < benchIterations; i++)
        {
            var r = weightSolver.Solve(benchInput, benchMatrix, benchCands, benchIdx, null, benchReqInputs);
            weightTotalNodes += r.Statistics.NodesExpanded;
        }
        swWeight.Stop();
        var weightNsPerNode = weightTotalNodes > 0 ? (double)swWeight.Elapsed.Ticks * 100 / weightTotalNodes : 0;

        _output.WriteLine("\n=== SO SÁNH CHI PHÍ NODE (Scenario Synthetic n=25) ===");
        _output.WriteLine($"Lexicographic: {lexNsPerNode:F1} ns/node ({lexTotalNodes:N0} nodes in {swLex.ElapsedMilliseconds} ms)");
        _output.WriteLine($"WeightedUtility: {weightNsPerNode:F1} ns/node ({weightTotalNodes:N0} nodes in {swWeight.ElapsedMilliseconds} ms)");
        _output.WriteLine($"Tỷ lệ chi phí (Weighted / Lexicographic): {(weightNsPerNode / Math.Max(1, lexNsPerNode)):F2}x");

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
