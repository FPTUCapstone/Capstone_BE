using System.Diagnostics;
using FluentAssertions;
using TripMate.Application.Features.Scheduling.Common;
using TripMate.Application.Features.Scheduling.Csp;
using TripMate.Application.Features.Scheduling.Csp.Objectives;
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

    public WeightedGoldenPolicyTests(ITestOutputHelper output)
    {
        _output = output;
    }

    private static (IReadOnlyList<GenerationCandidate> Candidates, IReadOnlyDictionary<long, int> Indices) Index(GenerationInput input)
    {
        var candidates = input.Candidates.OrderBy(c => c.Id).ToList();
        var indices = candidates.Select((c, i) => (c.Id, Index: i + 1)).ToDictionary(x => x.Id, x => x.Index);
        return (candidates, indices);
    }

    public static IEnumerable<object[]> GetBoundOnOffScenarios()
    {
        var scenarios = WeightedScenarioFactory.CreateAllScenarios();
        foreach (var s in scenarios)
        {
            yield return new object[] { s, false }; // Golden/Weighted (Constant TimeFit)
            yield return new object[] { s, true };  // Golden/Weighted-TimeFit (Real TimeFit)
        }
    }

    [Theory]
    [MemberData(nameof(GetBoundOnOffScenarios))]
    public void BoundOnOff_GivesSameBestOnCompletedSearch(GoldenScenario scenario, bool isTimeFit)
    {
        ArgumentNullException.ThrowIfNull(scenario);

        var primaryCtx = CspItinerarySolver.CreatePrimaryContext(
            scenario.Input, scenario.Matrix, scenario.Candidates, scenario.Indices, Options);

        var requestInputs = isTimeFit
            ? WeightedTimeFitInputsFactory.CreateRequestInputs(scenario.Candidates)
            : WeightedGoldenInputsFactory.CreateRequestInputs(scenario.Candidates);

        var inputs = ObjectiveKernelFactory.BuildWeightedInputs(primaryCtx, requestInputs);
        var nodeOf = primaryCtx.Nodes.Select((n, i) => (n.Candidate.Id, Index: i)).ToDictionary(x => x.Id, x => x.Index);

        var baseCspOpts = scenario.CspOptions ?? GoldenCspOptions;
        var maxNodes = scenario.Candidates.Count <= 10 ? 500_000 : baseCspOpts.MaxNodes;

        // 1. Solve with Bound OFF (Tắt cận)
        var optsUnbounded = new CspOptions
        {
            Mode = ObjectiveMode.WeightedUtility,
            DisableBound = true,
            TimeLimitMilliseconds = 120_000,
            MaxNodes = maxNodes,
            MaxStops = baseCspOpts.MaxStops,
            MaxOptionalDomainSize = baseCspOpts.MaxOptionalDomainSize,
            FinalCandidatesToValidate = baseCspOpts.FinalCandidatesToValidate,
            PolishWithLocalSearch = false
        };
        var solverUnbounded = new CspItinerarySolver(new ItineraryScheduleEvaluator(Options), Options, optsUnbounded);
        var resultUnbounded = solverUnbounded.Solve(
            scenario.Input,
            scenario.Matrix,
            scenario.Candidates,
            scenario.Indices,
            scenario.InitialIncumbent,
            weighted: requestInputs,
            cancellationToken: CancellationToken.None);

        var snapUnbounded = WeightedSnapshotMapper.From(scenario, resultUnbounded, primaryCtx, inputs, nodeOf);

        // 2. Solve with Bound ON (Bật cận)
        var optsBounded = new CspOptions
        {
            Mode = ObjectiveMode.WeightedUtility,
            DisableBound = false,
            TimeLimitMilliseconds = 120_000,
            MaxNodes = maxNodes,
            MaxStops = baseCspOpts.MaxStops,
            MaxOptionalDomainSize = baseCspOpts.MaxOptionalDomainSize,
            FinalCandidatesToValidate = baseCspOpts.FinalCandidatesToValidate,
            PolishWithLocalSearch = false
        };
        var solverBounded = new CspItinerarySolver(new ItineraryScheduleEvaluator(Options), Options, optsBounded);
        var resultBounded = solverBounded.Solve(
            scenario.Input,
            scenario.Matrix,
            scenario.Candidates,
            scenario.Indices,
            scenario.InitialIncumbent,
            weighted: requestInputs,
            cancellationToken: CancellationToken.None);

        var snapBounded = WeightedSnapshotMapper.From(scenario, resultBounded, primaryCtx, inputs, nodeOf);

        // Policy Assertions
        if (snapUnbounded.SearchCompleted)
        {
            snapBounded.SearchCompleted.Should().BeTrue(
                $"Kịch bản {scenario.Id} (TimeFit={isTimeFit}) tắt cận hoàn tất tìm kiếm thì bật cận cũng phải hoàn tất.");

            snapBounded.BestVisitPoiIds.Should().Equal(snapUnbounded.BestVisitPoiIds,
                $"Kịch bản {scenario.Id} (TimeFit={isTimeFit}) tắt cận hoàn tất thì bật cận phải tìm đúng nghiệm tối ưu.");

            snapBounded.BestModelObjective.Should().Be(snapUnbounded.BestModelObjective,
                $"Kịch bản {scenario.Id} (TimeFit={isTimeFit}) tắt cận hoàn tất thì bật cận phải đạt cùng BestModelObjective.");

            snapBounded.NodesExpanded.Should().BeLessThanOrEqualTo(snapUnbounded.NodesExpanded,
                $"Kịch bản {scenario.Id} (TimeFit={isTimeFit}) bật cận không được duyệt nhiều node hơn tắt cận.");
        }
        else
        {
            if (snapUnbounded.BestModelObjective.HasValue && snapBounded.BestModelObjective.HasValue)
            {
                // Khi tìm kiếm chưa hoàn thành, nghiệm tốt nhất được chọn (sau bước kiểm tra Evaluator)
                // của bản bật cận phải không kém hơn bản tắt cận (theo BestModelObjective hoặc BestEvaluatedObjective).
                var modelObjGte = snapBounded.BestModelObjective.Value >= snapUnbounded.BestModelObjective.Value;
                var evalObjGte = snapBounded.BestEvaluatedObjective.HasValue
                    && snapUnbounded.BestEvaluatedObjective.HasValue
                    && snapBounded.BestEvaluatedObjective.Value >= snapUnbounded.BestEvaluatedObjective.Value;

                (modelObjGte || evalObjGte).Should().BeTrue(
                    $"Kịch bản {scenario.Id} (TimeFit={isTimeFit}) khi tắt cận chưa xong, nghiệm tốt nhất của bản bật cận phải >= tắt cận " +
                    $"(ModelObj: {snapBounded.BestModelObjective} vs {snapUnbounded.BestModelObjective}, " +
                    $"EvalObj: {snapBounded.BestEvaluatedObjective} vs {snapUnbounded.BestEvaluatedObjective}).");
            }
        }
    }

    [Fact]
    public void MeasurePruningStatistics_And_NodeCost()
    {
        // Đo riêng kịch bản N=25 (tổng hợp 5 kịch bản n=25 ngẫu nhiên với seed khác nhau)
        _output.WriteLine("=== ĐO CẮT GIẢM TRÊN CÁC KỊCH BẢN N=25 ===");
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

        _output.WriteLine("=== SO SÁNH CHI PHÍ NODE (Scenario Synthetic n=25) ===");
        _output.WriteLine($"Lexicographic: {lexNsPerNode:F1} ns/node ({lexNodes:N0} nodes in {lexTimeMs} ms)");
        _output.WriteLine($"WeightedUtility: {weightNsPerNode:F1} ns/node ({weightNodes:N0} nodes in {weightTimeMs} ms)");
        _output.WriteLine($"Tỷ lệ chi phí (Weighted / Lexicographic): {(weightNsPerNode / Math.Max(1, lexNsPerNode)):F2}x");
    }
}
