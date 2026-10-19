using FluentAssertions;
using TripMate.Application.Features.Scheduling.Common;
using TripMate.Application.Features.Scheduling.Csp;
using TripMate.Application.Features.Scheduling.Csp.Objectives;
using TripMate.Application.Features.Scheduling.Personalization;
using TripMate.Application.Features.Scheduling.Routing;
using TripMate.Application.UnitTests.Features.Scheduling.Golden;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;
using TripMate.Application.Features.Scheduling.TimeOfDay;
using Xunit;
using Xunit.Abstractions;

namespace TripMate.Application.UnitTests.Features.Scheduling.Objectives;

public class ObjectiveCalculatorTests
{
    private readonly ITestOutputHelper _output;
    private static readonly SchedulingGenerationOptions Options = new();
    private static readonly CspOptions GoldenCspOptions = new() { TimeLimitMilliseconds = 120_000 };

    public ObjectiveCalculatorTests(ITestOutputHelper output)
    {
        _output = output;
    }

    /// <summary>
    /// TimeFit nhạy cảm với từng phút bắt đầu ghé điểm, dùng để khóa chặt bất biến giờ bắt đầu tham quan trong I2.
    /// Dùng modulo nguyên tố 101 để tránh chu kỳ chia hết cho các khoảng thời gian phổ biến (60 phút, 420 phút UTC+7, 1440 phút).
    /// </summary>
    private sealed class MinuteSensitiveTimeFit : ITimeFitProvider
    {
        public double Factor(int node, int startMin, int endMin)
        {
            return 1.0 + 0.001 * ((startMin * 7 + endMin * 13 + node * 31) % 101);
        }

        public double MaxFactor(int node) => 1.5;
        public double MaxFactorFrom(int node, int fromMinute) => 1.5;
    }

    private static (WeightedObjectiveInputs Inputs, IReadOnlyDictionary<long, int> NodeOf) CreateSensitiveInputs(
        RoutingContext ctx)
    {
        var n = ctx.Nodes.Count;
        var nodeOf = new Dictionary<long, int>(n);
        var baseScores = new double[n];
        var categoryIndex = new int[n];
        var isExposedOutdoor = new bool[n];
        var isFood = new bool[n];
        var isRestStop = new bool[n];
        var effort = new int[n];

        for (var v = 0; v < n; v++)
        {
            var poiId = ctx.Nodes[v].Candidate.Id;
            nodeOf[poiId] = v;
            baseScores[v] = 0.2 + 0.8 * ((poiId * 2654435761L & 0x7FFFFFFF) % 1000) / 1000.0;
            categoryIndex[v] = v % 3;

            isExposedOutdoor[v] = (v % 2) == 0;
            isFood[v] = (v % 4) == 0;
            isRestStop[v] = false;
            effort[v] = 20 + (v % 4) * 15;
        }

        var gammaScaled = new long[3][];
        for (var c = 0; c < 3; c++)
        {
            gammaScaled[c] = new long[20];
            for (var k = 0; k < 20; k++)
            {
                gammaScaled[c][k] = (long)Math.Round(Math.Pow(0.85, k) * WeightedObjectiveInputs.Scale);
            }
        }

        // Bật toàn bộ các khoản phạt mềm với ngưỡng thấp để kiểm tra toàn diện
        var personalParams = new PersonalParams(
            LambdaPerMin: 0.015,
            Mu: 0.005,
            MaxStopsPerDay: 2,
            MaxDailyEffort: 60,
            RestAfterMin: 60,
            MaxConsecutiveOutdoor: 1,
            SunriseFactor: 0.6,
            GammaDefault: 0.6,
            GammaLoved: 0.85,
            LovedCategories: new HashSet<string>(),
            RhoOutdoor: 0.15,
            RhoRest: 0.20,
            RhoStops: 0.25,
            RhoEffortPerHour: 0.05,
            ConfigVersion: "test-sensitive");

        var inputs = new WeightedObjectiveInputs
        {
            Base = baseScores,
            CategoryIndex = categoryIndex,
            CategoryCount = 3,
            GammaScaled = gammaScaled,
            IsExposedOutdoor = isExposedOutdoor,
            IsFood = isFood,
            IsRestStop = isRestStop,
            Effort = effort,
            TimeFit = new MinuteSensitiveTimeFit(),
            P = personalParams
        };

        return (inputs, nodeOf);
    }

    private static (WeightedObjectiveInputs Inputs, IReadOnlyDictionary<long, int> NodeOf) CreateRealTimeFitInputs(
        RoutingContext ctx)
    {
        var n = ctx.Nodes.Count;
        var nodeOf = new Dictionary<long, int>(n);
        var baseScores = new double[n];
        var categoryIndex = new int[n];
        var isExposedOutdoor = new bool[n];
        var isFood = new bool[n];
        var isRestStop = new bool[n];
        var effort = new int[n];

        for (var v = 0; v < n; v++)
        {
            var poiId = ctx.Nodes[v].Candidate.Id;
            nodeOf[poiId] = v;
            baseScores[v] = 0.2 + 0.8 * ((poiId * 2654435761L & 0x7FFFFFFF) % 1000) / 1000.0;
            categoryIndex[v] = v % 3;

            isExposedOutdoor[v] = (v % 2) == 0;
            isFood[v] = (v % 4) == 0;
            isRestStop[v] = false;
            effort[v] = 20 + (v % 4) * 15;
        }

        var gammaScaled = new long[3][];
        for (var c = 0; c < 3; c++)
        {
            gammaScaled[c] = new long[20];
            for (var k = 0; k < 20; k++)
            {
                gammaScaled[c][k] = (long)Math.Round(Math.Pow(0.85, k) * WeightedObjectiveInputs.Scale);
            }
        }

        var personalParams = new PersonalParams(
            LambdaPerMin: 0.015,
            Mu: 0.005,
            MaxStopsPerDay: 2,
            MaxDailyEffort: 60,
            RestAfterMin: 60,
            MaxConsecutiveOutdoor: 1,
            SunriseFactor: 0.6,
            GammaDefault: 0.6,
            GammaLoved: 0.85,
            LovedCategories: new HashSet<string>(),
            RhoOutdoor: 0.15,
            RhoRest: 0.20,
            RhoStops: 0.25,
            RhoEffortPerHour: 0.05,
            ConfigVersion: "test-real-timefit");

        var inputs = new WeightedObjectiveInputs
        {
            Base = baseScores,
            CategoryIndex = categoryIndex,
            CategoryCount = 3,
            GammaScaled = gammaScaled,
            IsExposedOutdoor = isExposedOutdoor,
            IsFood = isFood,
            IsRestStop = isRestStop,
            Effort = effort,
            TimeFit = new RealTimeFitProvider(ctx, personalParams),
            P = personalParams
        };

        return (inputs, nodeOf);
    }

    [Fact]
    public void I2_Evaluate_equals_Replay_on_all_24_non_rest_golden_scenarios()
    {
        string[] measuredScenarioIds =
        [
            "grid-c10-m0-none-s1", "grid-c10-m1-none-s1", "grid-c10-m3-none-s1", "grid-c10-m6-none-s1",
            "grid-c20-m0-none-s1", "grid-c20-m1-none-s1", "grid-c20-m3-none-s1", "grid-c20-m6-none-s1",
            "grid-c40-m0-none-s1", "grid-c40-m1-none-s1", "grid-c40-m3-none-s1", "grid-c40-m6-none-s1",
            "named-zigzag", "named-zigzag-240", "named-tail-infeasible", "named-asymmetric",
            "named-two-opt", "named-relocate", "named-reconsideration",
            "incumbent-valid-c20-m2-none-s10", "incumbent-invalid-rejected-s13",
            "budget-tight-exact-mandatory-s21",
            "boundary-fractional-seconds-s41", "boundary-urgent-slack-mrv-s42"
        ];

        var allScenarios = GoldenScenarioFactory.CreateAllScenarios();
        var nonRestScenarios = measuredScenarioIds
            .Select(id => allScenarios.First(s => s.Id == id))
            .ToList();

        nonRestScenarios.Count.Should().Be(24, "Must have exactly 24 measured non-rest golden scenarios");

        var evaluator = new ItineraryScheduleEvaluator(Options);
        var catScratch = new int[3];
        var itemScratch = new List<GeneratedItineraryItem>(32);

        foreach (var scenario in nonRestScenarios)
        {
            var cspOpts = scenario.CspOptions ?? GoldenCspOptions;
            var solver = new CspItinerarySolver(evaluator, Options, cspOpts);
            var result = solver.Solve(
                scenario.Input, scenario.Matrix, scenario.Candidates, scenario.Indices,
                scenario.InitialIncumbent, CancellationToken.None);

            result.Schedule.Should().NotBeNull($"Scenario {scenario.Id} must yield a valid schedule");
            var schedule = result.Schedule!;

            var primaryCtx = CspItinerarySolver.CreatePrimaryContext(
                scenario.Input, scenario.Matrix, scenario.Candidates, scenario.Indices, Options);

            var (inputs, nodeOf) = CreateSensitiveInputs(primaryCtx);

            var visitItems = schedule.Plan.Items
                .Where(i => i.Kind == ItineraryItemKind.Visit && i.PointOfInterestId.HasValue)
                .OrderBy(i => i.SequenceNo)
                .ToList();

            var seq = visitItems.Select(i => nodeOf[i.PointOfInterestId!.Value]).ToArray();
            var routeState = RouteState.Build(primaryCtx, seq);
            routeState.Should().NotBeNull($"RouteState.Build must succeed for scenario {scenario.Id}");

            // 1. Replay trên mô hình tìm kiếm
            var replay = ObjectiveCalculator.Replay(inputs, primaryCtx, routeState!.Sequence, routeState.Earliest, catScratch);

            // 2. Evaluate trên lịch đã qua Evaluator
            var evaluated = ObjectiveCalculator.Evaluate(
                inputs, primaryCtx, schedule, nodeOf, scenario.Indices, catScratch, itemScratch);

            _output.WriteLine($"[I2 PASS] {scenario.Id} | Stops: {visitItems.Count} | Replay: {replay} | Evaluate: {evaluated}");

            // Khẳng định bằng nhau tuyệt đối từng giá trị (bit-for-bit)
            evaluated.Should().Be(replay, $"Scenario {scenario.Id} must have Evaluate == Replay exactly");
        }
    }

    [Fact]
    public void I2_Evaluate_equals_Replay_on_all_24_non_rest_golden_scenarios_with_real_TimeFit()
    {
        string[] measuredScenarioIds =
        [
            "grid-c10-m0-none-s1", "grid-c10-m1-none-s1", "grid-c10-m3-none-s1", "grid-c10-m6-none-s1",
            "grid-c20-m0-none-s1", "grid-c20-m1-none-s1", "grid-c20-m3-none-s1", "grid-c20-m6-none-s1",
            "grid-c40-m0-none-s1", "grid-c40-m1-none-s1", "grid-c40-m3-none-s1", "grid-c40-m6-none-s1",
            "named-zigzag", "named-zigzag-240", "named-tail-infeasible", "named-asymmetric",
            "named-two-opt", "named-relocate", "named-reconsideration",
            "incumbent-valid-c20-m2-none-s10", "incumbent-invalid-rejected-s13",
            "budget-tight-exact-mandatory-s21",
            "boundary-fractional-seconds-s41", "boundary-urgent-slack-mrv-s42"
        ];

        var allScenarios = GoldenScenarioFactory.CreateAllScenarios();
        var nonRestScenarios = measuredScenarioIds
            .Select(id => allScenarios.First(s => s.Id == id))
            .ToList();

        nonRestScenarios.Count.Should().Be(24, "Must have exactly 24 measured non-rest golden scenarios");

        var evaluator = new ItineraryScheduleEvaluator(Options);
        var catScratch = new int[3];
        var itemScratch = new List<GeneratedItineraryItem>(32);

        foreach (var scenario in nonRestScenarios)
        {
            var cspOpts = scenario.CspOptions ?? GoldenCspOptions;
            var solver = new CspItinerarySolver(evaluator, Options, cspOpts);
            var result = solver.Solve(
                scenario.Input, scenario.Matrix, scenario.Candidates, scenario.Indices,
                scenario.InitialIncumbent, CancellationToken.None);

            result.Schedule.Should().NotBeNull($"Scenario {scenario.Id} must yield a valid schedule");
            var schedule = result.Schedule!;

            var primaryCtx = CspItinerarySolver.CreatePrimaryContext(
                scenario.Input, scenario.Matrix, scenario.Candidates, scenario.Indices, Options);

            var (inputs, nodeOf) = CreateRealTimeFitInputs(primaryCtx);

            var visitItems = schedule.Plan.Items
                .Where(i => i.Kind == ItineraryItemKind.Visit && i.PointOfInterestId.HasValue)
                .OrderBy(i => i.SequenceNo)
                .ToList();

            var seq = visitItems.Select(i => nodeOf[i.PointOfInterestId!.Value]).ToArray();
            var routeState = RouteState.Build(primaryCtx, seq);
            routeState.Should().NotBeNull($"RouteState.Build must succeed for scenario {scenario.Id}");

            // 1. Replay trên mô hình tìm kiếm
            var replay = ObjectiveCalculator.Replay(inputs, primaryCtx, routeState!.Sequence, routeState.Earliest, catScratch);

            // 2. Evaluate trên lịch đã qua Evaluator
            var evaluated = ObjectiveCalculator.Evaluate(
                inputs, primaryCtx, schedule, nodeOf, scenario.Indices, catScratch, itemScratch);

            _output.WriteLine($"[I2 Real TimeFit PASS] {scenario.Id} | Stops: {visitItems.Count} | Replay: {replay} | Evaluate: {evaluated}");

            // Khẳng định bằng nhau tuyệt đối từng giá trị (bit-for-bit)
            evaluated.Should().Be(replay, $"Scenario {scenario.Id} must have Evaluate == Replay exactly with real TimeFit");
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(60)]
    [InlineData(420)]
    [InlineData(1440)]
    public void I2_Sanity_Time_shift_causes_Evaluate_to_differ_from_Replay(int shiftMinutes)
    {
        var allScenarios = GoldenScenarioFactory.CreateAllScenarios();
        var targetScenario = allScenarios.First(s => s.Id == "grid-c20-m3-none-s1");

        var evaluator = new ItineraryScheduleEvaluator(Options);
        var cspOpts = targetScenario.CspOptions ?? GoldenCspOptions;
        var solver = new CspItinerarySolver(evaluator, Options, cspOpts);
        var result = solver.Solve(
            targetScenario.Input, targetScenario.Matrix, targetScenario.Candidates, targetScenario.Indices,
            targetScenario.InitialIncumbent, CancellationToken.None);

        var schedule = result.Schedule!;
        var primaryCtx = CspItinerarySolver.CreatePrimaryContext(
            targetScenario.Input, targetScenario.Matrix, targetScenario.Candidates, targetScenario.Indices, Options);

        var (inputs, nodeOf) = CreateSensitiveInputs(primaryCtx);
        var catScratch = new int[3];
        var itemScratch = new List<GeneratedItineraryItem>(32);

        var visitItems = schedule.Plan.Items
            .Where(i => i.Kind == ItineraryItemKind.Visit && i.PointOfInterestId.HasValue)
            .OrderBy(i => i.SequenceNo)
            .ToList();

        var seq = visitItems.Select(i => nodeOf[i.PointOfInterestId!.Value]).ToArray();
        var routeState = RouteState.Build(primaryCtx, seq)!;

        var replay = ObjectiveCalculator.Replay(inputs, primaryCtx, routeState.Sequence, routeState.Earliest, catScratch);

        // Cố ý dịch giờ đến và đi của điểm cuối cùng lên shiftMinutes phút
        var lastVisitIndex = schedule.Plan.Items.Count - 1;
        var modifiedItems = new List<GeneratedItineraryItem>();
        for (var i = 0; i < schedule.Plan.Items.Count; i++)
        {
            var item = schedule.Plan.Items.ElementAt(i);
            if (i == lastVisitIndex)
            {
                modifiedItems.Add(item with
                {
                    PlannedArrivalUtc = item.PlannedArrivalUtc.AddMinutes(shiftMinutes),
                    PlannedDepartureUtc = item.PlannedDepartureUtc.AddMinutes(shiftMinutes)
                });
            }
            else
            {
                modifiedItems.Add(item);
            }
        }

        var modifiedPlan = new GeneratedItineraryPlan(
            modifiedItems,
            schedule.EndAtUtc,
            schedule.TotalDurationMinutes,
            schedule.TotalEstimatedCost);

        var modifiedSchedule = new EvaluatedItinerarySchedule(
            modifiedPlan,
            schedule.TotalMatrixTravelMinutes,
            schedule.TotalDurationMinutes,
            schedule.EndAtUtc,
            schedule.TotalEstimatedCost,
            schedule.VisitPoiIds);

        var evaluatedShifted = ObjectiveCalculator.Evaluate(
            inputs, primaryCtx, modifiedSchedule, nodeOf, targetScenario.Indices, catScratch, itemScratch);

        _output.WriteLine($"[I2 SANITY] Shift: +{shiftMinutes}m | Original Replay: {replay} vs Shifted Evaluate: {evaluatedShifted}");

        evaluatedShifted.Should().NotBe(
            replay,
            $"A {shiftMinutes}-minute shift in visit time must produce a different objective score, proving I2 is sensitive to timing drift");
    }

    [Fact]
    public void I1_Leg_sum_equals_total_matrix_travel_on_rest_scenarios()
    {
        var allScenarios = GoldenScenarioFactory.CreateAllScenarios();
        var restScenarios = allScenarios
            .Where(s => s.Input.RestPreference != RestPreference.None && s.ExpectedFeasible)
            .Take(5)
            .ToList();

        var evaluator = new ItineraryScheduleEvaluator(Options);
        var catScratch = new int[3];
        var itemScratch = new List<GeneratedItineraryItem>(32);

        foreach (var scenario in restScenarios)
        {
            var cspOpts = scenario.CspOptions ?? GoldenCspOptions;
            var solver = new CspItinerarySolver(evaluator, Options, cspOpts);
            var result = solver.Solve(
                scenario.Input, scenario.Matrix, scenario.Candidates, scenario.Indices,
                scenario.InitialIncumbent, CancellationToken.None);

            if (result.Schedule is null) continue;

            var primaryCtx = CspItinerarySolver.CreatePrimaryContext(
                scenario.Input, scenario.Matrix, scenario.Candidates, scenario.Indices, Options);

            var (inputs, nodeOf) = CreateSensitiveInputs(primaryCtx);

            var action = () => ObjectiveCalculator.Evaluate(
                inputs, primaryCtx, result.Schedule, nodeOf, scenario.Indices, catScratch, itemScratch);

            action.Should().NotThrow("Evaluate must satisfy Invariant: legSum + returnRaw == TotalMatrixTravelMinutes");
        }
    }

    [Fact]
    public void I3_Rest_cafe_travel_is_counted()
    {
        var allScenarios = GoldenScenarioFactory.CreateAllScenarios();
        var scenario = allScenarios.First(s => s.Id == "grid-c20-m3-frequent-s1");

        var evaluator = new ItineraryScheduleEvaluator(Options);
        var cspOpts = scenario.CspOptions ?? GoldenCspOptions;
        var solver = new CspItinerarySolver(evaluator, Options, cspOpts);
        var result = solver.Solve(
            scenario.Input, scenario.Matrix, scenario.Candidates, scenario.Indices,
            scenario.InitialIncumbent, CancellationToken.None);

        result.Schedule.Should().NotBeNull();
        var schedule = result.Schedule!;

        var primaryCtx = CspItinerarySolver.CreatePrimaryContext(
            scenario.Input, scenario.Matrix, scenario.Candidates, scenario.Indices, Options);

        var (inputs, nodeOf) = CreateSensitiveInputs(primaryCtx);
        var catScratch = new int[3];
        var itemScratch = new List<GeneratedItineraryItem>(32);

        var j = ObjectiveCalculator.Evaluate(
            inputs, primaryCtx, schedule, nodeOf, scenario.Indices, catScratch, itemScratch);

        j.Should().NotBe(0);
    }

    [Fact]
    public void I4_Mandatory_visit_and_rest_in_same_schedule_counts_category_and_evaluates_without_error()
    {
        var allScenarios = GoldenScenarioFactory.CreateAllScenarios();
        var scenario = allScenarios.First(s => s.Id == "grid-c20-m3-frequent-s1");

        var evaluator = new ItineraryScheduleEvaluator(Options);
        var cspOpts = scenario.CspOptions ?? GoldenCspOptions;
        var solver = new CspItinerarySolver(evaluator, Options, cspOpts);
        var result = solver.Solve(
            scenario.Input, scenario.Matrix, scenario.Candidates, scenario.Indices,
            scenario.InitialIncumbent, CancellationToken.None);

        result.Schedule.Should().NotBeNull();
        var schedule = result.Schedule!;

        schedule.Plan.Items.Any(i => i.Kind == ItineraryItemKind.Rest).Should().BeTrue("Lịch kịch bản Frequent phải chứa Rest stop");
        schedule.Plan.Items.Any(i => i.Kind == ItineraryItemKind.Visit && scenario.Input.MandatoryPoiIds.Contains(i.PointOfInterestId ?? 0))
            .Should().BeTrue("Lịch phải chứa điểm tham quan bắt buộc");

        var primaryCtx = CspItinerarySolver.CreatePrimaryContext(
            scenario.Input, scenario.Matrix, scenario.Candidates, scenario.Indices, Options);

        var (inputs, nodeOf) = CreateSensitiveInputs(primaryCtx);
        var catScratch = new int[inputs.CategoryCount];
        var itemScratch = new List<GeneratedItineraryItem>(32);

        var j = ObjectiveCalculator.Evaluate(
            inputs, primaryCtx, schedule, nodeOf, scenario.Indices, catScratch, itemScratch);

        j.Should().NotBe(0);
        var totalVisitsCounted = catScratch.Sum();
        var actualVisitsInSchedule = schedule.Plan.Items.Count(i => i.Kind == ItineraryItemKind.Visit);
        totalVisitsCounted.Should().Be(
            actualVisitsInSchedule,
            "Chỉ điểm ghé tham quan (Visit) mới được tính vào catScratch, điểm nghỉ (Rest) không được cộng vào danh mục");
    }
}
