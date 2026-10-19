using FluentAssertions;
using TripMate.Application.Features.Scheduling.Common;
using TripMate.Application.Features.Scheduling.Csp;
using TripMate.Application.Features.Scheduling.Csp.Objectives;
using TripMate.Application.Features.Scheduling.Personalization;
using TripMate.Application.Features.Scheduling.Routing;
using TripMate.Application.UnitTests.Features.Scheduling.Golden;
using TripMate.Domain.Enums;
using Xunit;

namespace TripMate.Application.UnitTests.Features.Scheduling.Objectives;

public class WeightedUtilityKernelTests
{
    private static readonly SchedulingGenerationOptions Options = new();
    private static readonly CspOptions TestCspOptions = new() { MaxStops = 5, TimeLimitMilliseconds = 2500 };

    /// <summary>
    /// TimeFit thay đổi theo từng phút (startMin % 60) để kiểm tra tính nhạy cảm tuyệt đối với giờ bắt đầu tham quan.
    /// </summary>
    private sealed class MinuteSensitiveTimeFit : ITimeFitProvider
    {
        public double Factor(int node, int startMin, int endMin) =>
            1.0 + 0.005 * (startMin % 60) + 0.001 * (node % 10);

        public double MaxFactor(int node) => 1.5;
        public double MaxFactorFrom(int node, int fromMinute) => 1.5;
    }

    private static (WeightedObjectiveInputs Inputs, IReadOnlyDictionary<long, int> NodeOf) CreateSensitiveInputs(RoutingContext ctx)
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
            P = personalParams,
        };

        return (inputs, nodeOf);
    }

    private static (RoutingContext Ctx, WeightedObjectiveInputs Inputs, IReadOnlyDictionary<long, int> NodeOf) CreateSensitiveFixture()
    {
        var scenario = GoldenScenarioFactory.CreateAllScenarios().First(s => s.Id == "grid-c40-m1-none-s1");
        var ctx = CspItinerarySolver.CreatePrimaryContext(
            scenario.Input, scenario.Matrix, scenario.Candidates, scenario.Indices, Options);
        var (inputs, nodeOf) = CreateSensitiveInputs(ctx);
        return (ctx, inputs, nodeOf);
    }

    private static SearchPrecomputed CreatePrecomputed(RoutingContext ctx)
    {
        var n = ctx.Nodes.Count;
        var initialDomain = new int[n];
        var inInitialDomain = new bool[n];
        for (var i = 0; i < n; i++)
        {
            initialDomain[i] = i;
            inInitialDomain[i] = true;
        }

        return new SearchPrecomputed(
            new int[n],
            new int[n],
            new int[n],
            new int[n],
            new int[n],
            15,
            100,
            initialDomain,
            inInitialDomain);
    }

    [Fact]
    public void Kernel_Push_Pop_Matches_Replay_BitForBit_With_Sensitive_Inputs()
    {
        var (ctx, inputs, _) = CreateSensitiveFixture();
        var buffers = new SearchBuffers(10, ctx.Nodes.Count);
        var pre = CreatePrecomputed(ctx);

        var kernel = new WeightedUtilityKernel(inputs, TestCspOptions);
        var stats = new CspStatistics();
        kernel.Initialize(ctx, buffers, pre, stats);

        var catScratch = new int[inputs.CategoryCount];

        // 1. Push node 0
        var node0 = ctx.Nodes[0];
        var start0 = 15;
        buffers.Sequence[0] = 0;
        buffers.Departure[0] = start0 + node0.VisitMinutes;
        buffers.Contains[0] = true;
        kernel.OnPush(0, 0, start0, ctx.StartMatrixIndex);

        var key0 = kernel.PeekEliteKey(1, buffers.Departure[0], node0.MatrixIndex, 30, buffers.Departure[0] + 15);
        var replayedJ0 = ObjectiveCalculator.Replay(inputs, ctx, new[] { 0 }, new[] { start0 }, catScratch);
        ((long)Math.Round(key0.Utility * WeightedObjectiveInputs.Scale)).Should().Be(replayedJ0);

        // 2. Push node 1
        var node1 = ctx.Nodes[1];
        var start1 = buffers.Departure[0] + ctx.Travel(node0.MatrixIndex, node1.MatrixIndex);
        buffers.Sequence[1] = 1;
        buffers.Departure[1] = start1 + node1.VisitMinutes;
        buffers.Contains[1] = true;
        kernel.OnPush(1, 1, start1, node0.MatrixIndex);

        var key1 = kernel.PeekEliteKey(2, buffers.Departure[1], node1.MatrixIndex, 45, buffers.Departure[1] + 15);
        var replayedJ1 = ObjectiveCalculator.Replay(inputs, ctx, new[] { 0, 1 }, new[] { start0, start1 }, catScratch);
        ((long)Math.Round(key1.Utility * WeightedObjectiveInputs.Scale)).Should().Be(replayedJ1);

        // 3. Pop node 1
        buffers.Contains[1] = false;
        kernel.OnPop(1, 1);

        // Sau khi pop node 1, push node 2
        var node2 = ctx.Nodes[2];
        var start2 = buffers.Departure[0] + ctx.Travel(node0.MatrixIndex, node2.MatrixIndex);
        buffers.Sequence[1] = 2;
        buffers.Departure[1] = start2 + node2.VisitMinutes;
        buffers.Contains[2] = true;
        kernel.OnPush(1, 2, start2, node0.MatrixIndex);

        var key2 = kernel.PeekEliteKey(2, buffers.Departure[1], node2.MatrixIndex, 45, buffers.Departure[1] + 15);
        var replayedJ2 = ObjectiveCalculator.Replay(inputs, ctx, new[] { 0, 2 }, new[] { start0, start2 }, catScratch);
        ((long)Math.Round(key2.Utility * WeightedObjectiveInputs.Scale)).Should().Be(replayedJ2);
    }

    [Fact]
    public void Kernel_Incumbent_Comparison_And_Transfer_Works_Properly()
    {
        var (ctx, inputs, _) = CreateSensitiveFixture();
        var buffers = new SearchBuffers(10, ctx.Nodes.Count);
        var pre = CreatePrecomputed(ctx);

        var kernel1 = new WeightedUtilityKernel(inputs, TestCspOptions);
        kernel1.Initialize(ctx, buffers, pre, new CspStatistics());

        kernel1.HasIncumbent.Should().BeFalse();
        kernel1.CompareToIncumbent(1, 75, ctx.Nodes[0].MatrixIndex, 30).Should().Be(-1);

        // Chấp nhận incumbent ban đầu từ một RouteState hợp lệ
        var node0 = ctx.Nodes[0];
        var state0 = RouteState.Build(ctx, new[] { 0 })!;
        var start0 = state0.Earliest[0];
        buffers.Sequence[0] = 0;
        buffers.Departure[0] = start0 + node0.VisitMinutes;
        kernel1.OnPush(0, 0, start0, ctx.StartMatrixIndex);
        kernel1.AcceptIncumbent(1, buffers.Departure[0], node0.MatrixIndex, state0.TravelMinutes);
        kernel1.HasIncumbent.Should().BeTrue();
        kernel1.IncumbentObjectiveForTest.Should().NotBeNull();

        var token = kernel1.ExportIncumbent();
        token.Should().NotBeNull();
        var weightedToken = token as WeightedIncumbentToken;
        weightedToken.Should().NotBeNull();
        weightedToken!.VisitPoiIds.Should().Equal(node0.Candidate.Id);

        // Xuất sang Kernel 2: ImportIncumbent luôn phát lại (Replay) trên context nhận
        var kernel2 = new WeightedUtilityKernel(inputs, TestCspOptions);
        kernel2.Initialize(ctx, buffers, pre, new CspStatistics());
        kernel2.ImportIncumbent(token);
        kernel2.HasIncumbent.Should().BeTrue();
        kernel2.IncumbentObjectiveForTest.Should().Be(kernel1.IncumbentObjectiveForTest);

        // 4 Cấp độ so sánh của CompareToIncumbent:
        // Cấp 1: Node 1 có Utility khác -> so sánh theo Objective
        buffers.Sequence[0] = 1;
        buffers.Departure[0] = 75;
        var node1 = ctx.Nodes[1];
        var cmp1 = kernel1.CompareToIncumbent(1, 75, node1.MatrixIndex, 30);
        cmp1.Should().NotBe(0);

        // Cấp 4: Cùng node 0 (cùng Utility, Travel, Finish, Id) -> trả về 0 (hòa)
        buffers.Sequence[0] = 0;
        buffers.Departure[0] = start0 + node0.VisitMinutes;
        var cmpSame = kernel1.CompareToIncumbent(1, buffers.Departure[0], node0.MatrixIndex, state0.TravelMinutes);
        cmpSame.Should().Be(0);
    }

    [Fact]
    public void Incumbent_uses_model_objective_even_when_evaluated_objective_is_higher()
    {
        // Kiểm thử W1: Khi Evaluator chèn nghỉ ngơi, Incumbent vẫn chỉ lưu J mô hình (_pendingObjective)
        var allScenarios = GoldenScenarioFactory.CreateAllScenarios();
        var scenario = allScenarios.First(s => s.Id == "grid-c20-m3-frequent-s1");
        var evaluator = new ItineraryScheduleEvaluator(Options);
        var solver = new CspItinerarySolver(evaluator, Options, scenario.CspOptions ?? TestCspOptions);
        var result = solver.Solve(
            scenario.Input, scenario.Matrix, scenario.Candidates, scenario.Indices,
            scenario.InitialIncumbent, CancellationToken.None);

        result.Schedule.Should().NotBeNull();
        var schedule = result.Schedule!;
        schedule.Plan.Items.Any(i => i.Kind == ItineraryItemKind.Rest).Should().BeTrue();

        var primaryCtx = CspItinerarySolver.CreatePrimaryContext(
            scenario.Input, scenario.Matrix, scenario.Candidates, scenario.Indices, Options);
        var (inputs, nodeOf) = CreateSensitiveInputs(primaryCtx);

        var buffers = new SearchBuffers(10, primaryCtx.Nodes.Count);
        var pre = CreatePrecomputed(primaryCtx);
        var kernel = new WeightedUtilityKernel(inputs, TestCspOptions);
        kernel.Initialize(primaryCtx, buffers, pre, new CspStatistics());

        // Thiết lập bộ đệm để khớp với các điểm của schedule
        var visitPoiIds = schedule.VisitPoiIds;
        for (var i = 0; i < visitPoiIds.Count; i++)
        {
            var nodeIdx = nodeOf[visitPoiIds[i]];
            buffers.Sequence[i] = nodeIdx;
            buffers.Departure[i] = 60 * (i + 1);
            buffers.Contains[nodeIdx] = true;
            kernel.OnPush(i, nodeIdx, 60 * i, i == 0 ? primaryCtx.StartMatrixIndex : primaryCtx.Nodes[buffers.Sequence[i - 1]].MatrixIndex);
        }

        var lastNode = primaryCtx.Nodes[buffers.Sequence[visitPoiIds.Count - 1]];
        var cmp = kernel.CompareToIncumbent(visitPoiIds.Count, buffers.Departure[visitPoiIds.Count - 1], lastNode.MatrixIndex, schedule.TotalMatrixTravelMinutes);
        cmp.Should().Be(-1); // Không có incumbent trước đó nên luôn thắng

        // Gọi CompareEvaluated: trả về _pendingComparison mà KHÔNG tính J thật
        kernel.CompareEvaluated(schedule).Should().Be(cmp);

        // Chấp nhận evaluated: Incumbent PHẢI mang J mô hình, KHÔNG phải J thật từ Evaluator
        buffers.Depth = visitPoiIds.Count;
        kernel.AcceptEvaluated(schedule);

        var modelJ = kernel.PeekEliteKey(visitPoiIds.Count, buffers.Departure[visitPoiIds.Count - 1], lastNode.MatrixIndex, schedule.TotalMatrixTravelMinutes, buffers.Departure[visitPoiIds.Count - 1] + 15);
        ((long)Math.Round(modelJ.Utility * WeightedObjectiveInputs.Scale)).Should().Be(kernel.IncumbentObjectiveForTest!.Value);

        // Kiểm tra thực tế: J thật của lịch chèn nghỉ khác với J mô hình
        var catScratch = new int[inputs.CategoryCount];
        var itemScratch = new List<GeneratedItineraryItem>(32);
        var realJ = ObjectiveCalculator.Evaluate(inputs, primaryCtx, schedule, nodeOf, scenario.Indices, catScratch, itemScratch);

        // Ghi nhận điểm chênh lệch và khẳng định Incumbent lưu đúng modelJ chứ không phải realJ
        kernel.IncumbentObjectiveForTest!.Value.Should().NotBe(realJ, "Incumbent phải dùng J mô hình để tránh cắt nhầm nhánh ở PR-07 khi TimeFit không đơn điệu");
    }

    [Fact]
    public void Kernel_Comparison_And_EliteOrdering_Have_Same_Sign()
    {
        // Kiểm thử W3: Thứ tự theo ComparePendingToIncumbent và theo WeightedEliteOrdering.Compare phải cùng dấu
        var (ctx, inputs, _) = CreateSensitiveFixture();
        var scratch = new int[inputs.CategoryCount];
        var ordering = new WeightedEliteOrdering(inputs, ctx, scratch);

        var state1 = RouteState.Build(ctx, new[] { 0, 1 });
        var state2 = RouteState.Build(ctx, new[] { 0, 2 });
        state1.Should().NotBeNull();
        state2.Should().NotBeNull();

        var key1 = ordering.CreateKey(state1!);
        var key2 = ordering.CreateKey(state2!);

        var eliteCmp = ordering.Compare(state1!, key1, state2!, key2);

        // Thiết lập kernel với incumbent là state2
        var buffers = new SearchBuffers(10, ctx.Nodes.Count);
        var pre = CreatePrecomputed(ctx);
        var kernel = new WeightedUtilityKernel(inputs, TestCspOptions);
        kernel.Initialize(ctx, buffers, pre, new CspStatistics());

        // Nạp state2 làm incumbent
        var token2 = new WeightedIncumbentToken(new[] { ctx.Nodes[0].Candidate.Id, ctx.Nodes[2].Candidate.Id });
        kernel.ImportIncumbent(token2);

        // Đẩy state1 vào buffers và so sánh
        buffers.Sequence[0] = 0;
        buffers.Departure[0] = state1!.Earliest[0] + ctx.Nodes[0].VisitMinutes;
        kernel.OnPush(0, 0, state1.Earliest[0], ctx.StartMatrixIndex);

        buffers.Sequence[1] = 1;
        buffers.Departure[1] = state1.Earliest[1] + ctx.Nodes[1].VisitMinutes;
        kernel.OnPush(1, 1, state1.Earliest[1], ctx.Nodes[0].MatrixIndex);

        var kernelCmp = kernel.CompareToIncumbent(2, buffers.Departure[1], ctx.Nodes[1].MatrixIndex, state1.TravelMinutes);

        Math.Sign(kernelCmp).Should().Be(Math.Sign(eliteCmp), "Thứ tự so sánh của Incumbent và EliteOrdering phải có cùng dấu");
    }

    [Fact]
    public void Kernel_CanImprove_Is_Unpruned_In_D2()
    {
        var (ctx, inputs, _) = CreateSensitiveFixture();
        var buffers = new SearchBuffers(10, ctx.Nodes.Count);
        var pre = CreatePrecomputed(ctx);

        var kernel = new WeightedUtilityKernel(inputs, TestCspOptions);
        kernel.Initialize(ctx, buffers, pre, new CspStatistics());

        kernel.CanImprove(new[] { 1, 2 }, 2, 0, 0, ctx.StartMatrixIndex).Should().BeTrue();
        kernel.CanImprove(Array.Empty<int>(), 0, 0, 0, ctx.StartMatrixIndex).Should().BeTrue();
    }

    [Fact]
    public void WeightedScheduleComparer_Sorts_Properly_And_Uses_Cache()
    {
        var scenario = GoldenScenarioFactory.CreateAllScenarios().First(s => s.Id == "grid-c40-m1-none-s1");
        var evaluator = new ItineraryScheduleEvaluator(Options);
        var solver = new CspItinerarySolver(evaluator, Options, TestCspOptions);
        var result = solver.Solve(
            scenario.Input, scenario.Matrix, scenario.Candidates, scenario.Indices,
            scenario.InitialIncumbent, CancellationToken.None);

        result.Schedule.Should().NotBeNull();
        var scheduleA = result.Schedule!;

        var solver2 = new CspItinerarySolver(evaluator, Options, new CspOptions { MaxStops = 2, TimeLimitMilliseconds = 2500 });
        var result2 = solver2.Solve(
            scenario.Input, scenario.Matrix, scenario.Candidates, scenario.Indices,
            null, CancellationToken.None);

        result2.Schedule.Should().NotBeNull();
        var scheduleB = result2.Schedule!;

        var (ctx, inputs, nodeOf) = CreateSensitiveFixture();
        var catScratch = new int[inputs.CategoryCount];
        var itemScratch = new List<GeneratedItineraryItem>(32);

        var comparer = new WeightedScheduleComparer(
            inputs, ctx, nodeOf, scenario.Indices, catScratch, itemScratch);

        // Schedule so với chính nó phải bằng 0
        comparer.Compare(scheduleA, scheduleA).Should().Be(0);
        comparer.Compare(scheduleB, scheduleB).Should().Be(0);
        comparer.Compare(null, null).Should().Be(0);
        comparer.Compare(null, scheduleA).Should().Be(1);
        comparer.Compare(scheduleA, null).Should().Be(-1);

        // Tính đối xứng
        var cmpAB = comparer.Compare(scheduleA, scheduleB);
        var cmpBA = comparer.Compare(scheduleB, scheduleA);
        cmpAB.Should().Be(-cmpBA);

        // Gọi lại nhiều lần để kiểm tra cache ConditionalWeakTable hoạt động trơn tru
        for (var i = 0; i < 5; i++)
        {
            comparer.Compare(scheduleA, scheduleB).Should().Be(cmpAB);
            comparer.Compare(scheduleB, scheduleA).Should().Be(cmpBA);
        }
    }
}
