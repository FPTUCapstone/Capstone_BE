using System.Diagnostics;
using System.Text.Json;
using FluentAssertions;
using TripMate.Application.Features.Scheduling.Common;
using TripMate.Application.Features.Scheduling.Csp;
using TripMate.Application.UnitTests.Features.Scheduling.Fixtures;
using TripMate.Application.UnitTests.Features.Scheduling.Golden.WeightedTimeFit;
using TripMate.Domain.Enums;
using Xunit;
using Xunit.Abstractions;

namespace TripMate.Application.UnitTests.Features.Scheduling.Golden.Weighted;

public class WeightedGoldenPolicyTests
{
    private readonly ITestOutputHelper _output;
    private static readonly SchedulingGenerationOptions Options = new();
    private static readonly CspOptions GoldenCspOptions = new() { TimeLimitMilliseconds = 120_000 };

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
    };

    public WeightedGoldenPolicyTests(ITestOutputHelper output)
    {
        _output = output;
    }

    private static string GetBaselineDirectory()
    {
        var dir = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "../../../Features/Scheduling/Golden/Weighted/Baseline-PR06"));
        return dir;
    }

    private static string GetBaselinePR07Directory() =>
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../Features/Scheduling/Golden/Weighted/Baseline-PR07"));

    private static string GetBaselinePR08Directory() =>
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../Features/Scheduling/Golden/Weighted-TimeFit/Baseline-PR08"));

    private static (IReadOnlyList<GenerationCandidate> Candidates, IReadOnlyDictionary<long, int> Indices) Index(GenerationInput input)
    {
        var candidates = input.Candidates.OrderBy(c => c.Id).ToList();
        var indices = candidates.Select((c, i) => (c.Id, Index: i + 1)).ToDictionary(x => x.Id, x => x.Index);
        return (candidates, indices);
    }

    public static IEnumerable<object[]> GetScenarios() =>
        WeightedScenarioFactory.CreateAllScenarios().Select(s => new object[] { s });

    [Theory]
    [MemberData(nameof(GetScenarios))]
    public void Solver_SatisfiesPruningPolicy_ComparedToBaselinePR06(GoldenScenario scenario)
    {
        ArgumentNullException.ThrowIfNull(scenario);

        var baselineDir = GetBaselineDirectory();
        var baselineFile = Path.Combine(baselineDir, $"{scenario.Id}.json");
        File.Exists(baselineFile).Should().BeTrue($"Baseline file {baselineFile} must exist.");

        var baselineJson = File.ReadAllText(baselineFile);
        var baseline = JsonSerializer.Deserialize<WeightedSolverGoldenSnapshot>(baselineJson, JsonOptions)!;

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

        var actual = WeightedSnapshotMapper.From(scenario, result, primaryCtx, inputs, nodeOf);

        // Policy 1: Đối với các kịch bản SearchCompleted == true trong baseline
        if (baseline.SearchCompleted)
        {
            // Nghiệm tối ưu không bị cắt nhầm: cùng BestVisitPoiIds và cùng BestModelObjective
            actual.BestVisitPoiIds.Should().Equal(baseline.BestVisitPoiIds,
                $"Kịch bản {scenario.Id} hoàn thành tìm kiếm nhưng nghiệm tốt nhất bị lệch.");

            actual.BestModelObjective.Should().Be(baseline.BestModelObjective,
                $"Kịch bản {scenario.Id} có hàm mục tiêu mô hình bị lệch.");

            // NodesExpanded không được tăng
            actual.NodesExpanded.Should().BeLessThanOrEqualTo(baseline.NodesExpanded,
                $"Kịch bản {scenario.Id} duyệt nhiều node hơn baseline khi có bound.");
        }

        // Policy 2: Đối với các kịch bản NodeLimitReached == true trong baseline
        if (baseline.NodeLimitReached && baseline.BestModelObjective.HasValue && actual.BestModelObjective.HasValue)
        {
            actual.BestModelObjective.Value.Should().BeGreaterThanOrEqualTo(baseline.BestModelObjective.Value,
                $"Kịch bản {scenario.Id} chạm node limit nhưng mục tiêu kém hơn baseline.");
        }
    }

    [Theory]
    [MemberData(nameof(GetScenarios))]
    public void Solver_SatisfiesPruningPolicy_ComparedToBaselinePR07(GoldenScenario scenario)
    {
        ArgumentNullException.ThrowIfNull(scenario);

        var baselineDir = GetBaselinePR07Directory();
        var baselineFile = Path.Combine(baselineDir, $"{scenario.Id}.json");
        File.Exists(baselineFile).Should().BeTrue($"Baseline file {baselineFile} must exist.");

        var baselineJson = File.ReadAllText(baselineFile);
        var baseline = JsonSerializer.Deserialize<WeightedSolverGoldenSnapshot>(baselineJson, JsonOptions)!;

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

        var actual = WeightedSnapshotMapper.From(scenario, result, primaryCtx, inputs, nodeOf);

        if (baseline.SearchCompleted)
        {
            actual.BestVisitPoiIds.Should().Equal(baseline.BestVisitPoiIds,
                $"Kịch bản {scenario.Id} hoàn thành tìm kiếm nhưng nghiệm tốt nhất bị lệch so với PR-07.");

            actual.BestModelObjective.Should().Be(baseline.BestModelObjective,
                $"Kịch bản {scenario.Id} có hàm mục tiêu mô hình bị lệch so với PR-07.");

            actual.NodesExpanded.Should().BeLessThanOrEqualTo(baseline.NodesExpanded,
                $"Kịch bản {scenario.Id} duyệt nhiều node hơn baseline PR-07 khi có bound.");
        }

        if (baseline.NodeLimitReached && baseline.BestModelObjective.HasValue && actual.BestModelObjective.HasValue)
        {
            actual.BestModelObjective.Value.Should().BeGreaterThanOrEqualTo(baseline.BestModelObjective.Value,
                $"Kịch bản {scenario.Id} chạm node limit nhưng mục tiêu kém hơn baseline PR-07.");
        }
    }

    [Theory]
    [MemberData(nameof(GetScenarios))]
    public void Solver_SatisfiesPruningPolicy_ComparedToBaselinePR08(GoldenScenario scenario)
    {
        ArgumentNullException.ThrowIfNull(scenario);

        var baselineDir = GetBaselinePR08Directory();
        var baselineFile = Path.Combine(baselineDir, $"{scenario.Id}.json");
        File.Exists(baselineFile).Should().BeTrue($"Baseline file {baselineFile} must exist.");

        var baselineJson = File.ReadAllText(baselineFile);
        var baseline = JsonSerializer.Deserialize<WeightedSolverGoldenSnapshot>(baselineJson, JsonOptions)!;

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

        var actual = WeightedSnapshotMapper.From(scenario, result, primaryCtx, inputs, nodeOf);

        if (baseline.SearchCompleted)
        {
            actual.BestVisitPoiIds.Should().Equal(baseline.BestVisitPoiIds,
                $"Kịch bản {scenario.Id} hoàn thành tìm kiếm nhưng nghiệm tốt nhất bị lệch so với PR-08.");

            actual.BestModelObjective.Should().Be(baseline.BestModelObjective,
                $"Kịch bản {scenario.Id} có hàm mục tiêu mô hình bị lệch so với PR-08.");

            actual.NodesExpanded.Should().BeLessThanOrEqualTo(baseline.NodesExpanded,
                $"Kịch bản {scenario.Id} duyệt nhiều node hơn baseline PR-08 khi có bound.");
        }

        if (baseline.NodeLimitReached && baseline.BestModelObjective.HasValue && actual.BestModelObjective.HasValue)
        {
            actual.BestModelObjective.Value.Should().BeGreaterThanOrEqualTo(baseline.BestModelObjective.Value,
                $"Kịch bản {scenario.Id} chạm node limit nhưng mục tiêu kém hơn baseline PR-08.");
        }
    }

    [Fact]
    public void MeasurePruningStatistics_And_NodeCost()
    {
        var baselineDir = GetBaselineDirectory();
        var scenarios = WeightedScenarioFactory.CreateAllScenarios();


        long totalBaselineNodes = 0;
        long totalActualNodes = 0;
        long totalPrunedByBound = 0;
        var nodesByCandCount = new Dictionary<int, (long Baseline, long Actual, int Count)>();

        foreach (var scenario in scenarios)
        {
            var baselineFile = Path.Combine(baselineDir, $"{scenario.Id}.json");
            var baselineJson = File.ReadAllText(baselineFile);
            var baseline = JsonSerializer.Deserialize<WeightedSolverGoldenSnapshot>(baselineJson, JsonOptions)!;

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

            totalBaselineNodes += baseline.NodesExpanded;
            totalActualNodes += result.Statistics.NodesExpanded;
            totalPrunedByBound += result.Statistics.PrunedByBound;

            var candCount = scenario.Candidates.Count;
            if (!nodesByCandCount.ContainsKey(candCount))
            {
                nodesByCandCount[candCount] = (0, 0, 0);
            }
            var currentGroup = nodesByCandCount[candCount];
            nodesByCandCount[candCount] = (
                currentGroup.Baseline + baseline.NodesExpanded,
                currentGroup.Actual + result.Statistics.NodesExpanded,
                currentGroup.Count + 1);
        }

        var totalReductionPercent = totalBaselineNodes > 0 ? (1.0 - (double)totalActualNodes / totalBaselineNodes) * 100.0 : 0.0;

        _output.WriteLine($"=== KẾT QUẢ CẮT TỈA TRÊN 67 SCENARIO ===");
        _output.WriteLine($"Tổng node baseline PR-06: {totalBaselineNodes:N0}");
        _output.WriteLine($"Tổng node sau khi có Bound: {totalActualNodes:N0}");
        _output.WriteLine($"Tổng nhánh bị PrunedByBound: {totalPrunedByBound:N0}");
        _output.WriteLine($"Tỷ lệ giảm node toàn bộ: {totalReductionPercent:F2}%");
        _output.WriteLine($"--- Phân tích theo số lượng ứng viên N ---");
        foreach (var kvp in nodesByCandCount.OrderBy(x => x.Key))
        {
            var red = kvp.Value.Baseline > 0 ? (1.0 - (double)kvp.Value.Actual / kvp.Value.Baseline) * 100.0 : 0.0;
            _output.WriteLine($"N={kvp.Key,2} ({kvp.Value.Count,2} kịch bản): Baseline={kvp.Value.Baseline,8:N0} -> Actual={kvp.Value.Actual,8:N0} (Giảm: {red,6:F2}%)");
        }

        // Đo riêng kịch bản N=25 (tổng hợp 5 kịch bản n=25 ngẫu nhiên với seed khác nhau)
        _output.WriteLine($"=== ĐO CẮT GIẢM TRÊN CÁC KỊCH BẢN N=25 ===");
        long n25Unbounded = 0;
        long n25Bounded = 0;
        long n25Pruned = 0;

        for (var seed = 101; seed <= 105; seed++)
        {
            var (input25, matrix25) = OptionalRouteOptimizationScenarios.CreateSyntheticCorpusScenario(
                candidateCount: 25,
                mandatoryCount: 2,
                seed: seed,
                restPreference: RestPreference.None);
            var (cands25, idx25) = Index(input25);
            var reqInputs25 = WeightedGoldenInputsFactory.CreateRequestInputs(cands25);

            var optsUnbounded = new CspOptions
            {
                Mode = ObjectiveMode.WeightedUtility,
                DisableBound = true,
                MaxNodes = 50_000,
                TimeLimitMilliseconds = 60_000,
                PolishWithLocalSearch = false
            };
            var optsBounded = new CspOptions
            {
                Mode = ObjectiveMode.WeightedUtility,
                DisableBound = false,
                MaxNodes = 50_000,
                TimeLimitMilliseconds = 60_000,
                PolishWithLocalSearch = false
            };

            var solverUnbounded = new CspItinerarySolver(new ItineraryScheduleEvaluator(Options), Options, optsUnbounded);
            var solverBounded = new CspItinerarySolver(new ItineraryScheduleEvaluator(Options), Options, optsBounded);

            var rUnbounded = solverUnbounded.Solve(input25, matrix25, cands25, idx25, initialIncumbent: null, weighted: reqInputs25);
            var rBounded = solverBounded.Solve(input25, matrix25, cands25, idx25, initialIncumbent: null, weighted: reqInputs25);

            n25Unbounded += rUnbounded.Statistics.NodesExpanded;
            n25Bounded += rBounded.Statistics.NodesExpanded;
            n25Pruned += rBounded.Statistics.PrunedByBound;

            var red = rUnbounded.Statistics.NodesExpanded > 0
                ? (1.0 - (double)rBounded.Statistics.NodesExpanded / rUnbounded.Statistics.NodesExpanded) * 100.0
                : 0.0;
            _output.WriteLine($"Seed {seed}: Unbounded={rUnbounded.Statistics.NodesExpanded,6:N0} -> Bounded={rBounded.Statistics.NodesExpanded,6:N0} (Giảm: {red,5:F2}%, PrunedByBound={rBounded.Statistics.PrunedByBound:N0})");
        }

        var totalN25Red = n25Unbounded > 0 ? (1.0 - (double)n25Bounded / n25Unbounded) * 100.0 : 0.0;
        _output.WriteLine($"Tổng n=25: Unbounded={n25Unbounded:N0} -> Bounded={n25Bounded:N0} (Giảm trung bình: {totalN25Red:F2}%, PrunedByBound: {n25Pruned:N0})");

        // Benchmark so sánh chi phí/node giữa Weighted và Lexicographic trên scenario n=25
        var (testInput, testMatrix) = OptionalRouteOptimizationScenarios.CreateSyntheticCorpusScenario(
            candidateCount: 25,
            mandatoryCount: 2,
            seed: 101,
            restPreference: RestPreference.None);
        var (testCands, testIdx) = Index(testInput);
        var testReqInputs = WeightedGoldenInputsFactory.CreateRequestInputs(testCands);

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
        lexSolver.Solve(testInput, testMatrix, testCands, testIdx, initialIncumbent: null);
        weightSolver.Solve(testInput, testMatrix, testCands, testIdx, initialIncumbent: null, weighted: testReqInputs);

        const int iterations = 10;
        var sw = Stopwatch.StartNew();
        long lexNodes = 0;
        for (var i = 0; i < iterations; i++)
        {
            var r = lexSolver.Solve(testInput, testMatrix, testCands, testIdx, initialIncumbent: null);
            lexNodes += r.Statistics.NodesExpanded;
        }
        sw.Stop();
        var lexTimeMs = sw.ElapsedMilliseconds;
        var lexNsPerNode = lexNodes > 0 ? (double)sw.Elapsed.Ticks * 100 / lexNodes : 0;

        sw.Restart();
        long weightNodes = 0;
        for (var i = 0; i < iterations; i++)
        {
            var r = weightSolver.Solve(testInput, testMatrix, testCands, testIdx, initialIncumbent: null, weighted: testReqInputs);
            weightNodes += r.Statistics.NodesExpanded;
        }
        sw.Stop();
        var weightTimeMs = sw.ElapsedMilliseconds;
        var weightNsPerNode = weightNodes > 0 ? (double)sw.Elapsed.Ticks * 100 / weightNodes : 0;

        _output.WriteLine($"=== SO SÁNH CHI PHÍ NODE (Scenario Synthetic n=25) ===");
        _output.WriteLine($"Lexicographic: {lexNsPerNode:F1} ns/node ({lexNodes:N0} nodes in {lexTimeMs} ms)");
        _output.WriteLine($"WeightedUtility: {weightNsPerNode:F1} ns/node ({weightNodes:N0} nodes in {weightTimeMs} ms)");
        _output.WriteLine($"Tỷ lệ chi phí (Weighted / Lexicographic): {(weightNsPerNode / Math.Max(1, lexNsPerNode)):F2}x");
    }
}
