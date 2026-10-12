using System.Diagnostics;

using FluentAssertions;

using TripMate.Application.Common.Models;
using TripMate.Application.Features.Scheduling.Common;
using TripMate.Application.Features.Scheduling.Csp;
using TripMate.Application.Features.Scheduling.Routing;
using TripMate.Application.UnitTests.Features.Scheduling.Fixtures;
using TripMate.Domain.Enums;

namespace TripMate.Application.UnitTests.Features.Scheduling.Csp;

public class CspItinerarySolverTests
{
    private static readonly SchedulingGenerationOptions Options = new();

    private static readonly CspOptions Exhaustive = new()
    {
        MaxStops = 99,
        MaxOptionalDomainSize = 99,
        MaxNodes = 10_000_000,
        TimeLimitMilliseconds = 60_000,
    };

    /// <summary>
    /// Forward checking và branch and bound không được bỏ sót lời giải: trên bài nhỏ, CSP phải cho đúng lịch tốt nhất
    /// theo product rule của phép duyệt vét cạn không cắt tỉa (R1), hoặc cùng kết luận vô nghiệm.
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    [InlineData(9)]
    [InlineData(10)]
    [InlineData(11)]
    [InlineData(12)]
    public void Solve_MatchesExhaustiveSearch_OnSmallInstances(int seed)
    {
        var (input, matrix) = SmallScenario(seed);
        var (candidates, indices) = Index(input);
        var ctx = RoutingContext.Create(input, matrix, candidates, indices, Options, Options.MiniRouting);
        var exhaustive = CspProductRuleOracle.Best(input, matrix, Options, ctx);

        var result = new CspItinerarySolver(new ItineraryScheduleEvaluator(Options), Options, Exhaustive)
            .Solve(input, matrix, candidates, indices);

        result.Statistics.SearchCompleted.Should().BeTrue();
        if (exhaustive is null)
        {
            result.ProvedInfeasible.Should().BeTrue();
        }
        else
        {
            result.Schedule.Should().NotBeNull();
            CspProductRuleOracle.Comparer(input, matrix).Compare(result.Schedule, exhaustive).Should().Be(0);
        }
    }

    [Theory]
    [InlineData(20, 2, 41)]
    [InlineData(40, 4, 42)]
    public void Solve_ReturnsValidScheduleWithAllMandatoryPois(int candidateCount, int mandatoryCount, int seed)
    {
        var (input, matrix) = OptionalRouteOptimizationScenarios.CreateSyntheticCorpusScenario(candidateCount, mandatoryCount, seed);
        var (candidates, indices) = Index(input);

        var nodeLimitedOnly = new CspOptions { TimeLimitMilliseconds = 600_000 };

        var result = new CspItinerarySolver(new ItineraryScheduleEvaluator(Options), Options, nodeLimitedOnly)
            .Solve(input, matrix, candidates, indices);

        result.Schedule.Should().NotBeNull();
        result.Schedule!.VisitPoiIds.Should().Contain(input.MandatoryPoiIds);
        var validate = () => new GeneratedItineraryInvariantValidator(Options).Validate(input, matrix, candidates, result.Schedule.Plan);
        validate.Should().NotThrow();
    }

    [Fact]
    public void Solve_ProvesOptimality_AndPrunes_WhenOpeningHoursAreTight()
    {
        var (input, matrix) = TightScenario(5);
        var (candidates, indices) = Index(input);

        var result = new CspItinerarySolver(new ItineraryScheduleEvaluator(Options), Options).Solve(input, matrix, candidates, indices);

        result.Schedule.Should().NotBeNull();
        result.Statistics.SearchCompleted.Should().BeTrue();
        (result.Statistics.PrunedByForwardChecking + result.Statistics.PrunedByBound).Should().BeGreaterThan(0);
    }

    [Fact]
    public void Solve_ProvesInfeasibility_AndNamesTheConflictingMandatoryPoi()
    {
        var (input, matrix) = OptionalRouteOptimizationScenarios.CreateSyntheticCorpusScenario(10, 2, 51);
        var closedId = input.MandatoryPoiIds.First();
        input = input with
        {
            Candidates = input.Candidates.Select(c => c.Id == closedId ? c with { OpeningHours = [] } : c).ToArray(),
        };
        var (candidates, indices) = Index(input);

        var result = new CspItinerarySolver(new ItineraryScheduleEvaluator(Options), Options).Solve(input, matrix, candidates, indices);

        result.ProvedInfeasible.Should().BeTrue();
        result.ConflictingMandatoryPoiIds.Should().Contain(closedId);
    }

    [Fact]
    public void Solve_ProvesInfeasibility_WhenTwoMandatoryPoisAreOpenOnlyAtTheSameHour()
    {
        var (input, matrix) = OptionalRouteOptimizationScenarios.CreateSyntheticCorpusScenario(8, 2, 52);
        var oneHour = new GenerationOpeningHours(2, new TimeOnly(9, 0), new TimeOnly(10, 0));
        var mandatory = input.MandatoryPoiIds.ToHashSet();
        input = input with
        {
            Candidates = input.Candidates
                .Select(c => mandatory.Contains(c.Id) ? c with { VisitDurationMinutes = 45, OpeningHours = [oneHour] } : c)
                .ToArray(),
        };
        var (candidates, indices) = Index(input);

        var result = new CspItinerarySolver(new ItineraryScheduleEvaluator(Options), Options).Solve(input, matrix, candidates, indices);

        result.ProvedInfeasible.Should().BeTrue();
        result.ConflictingMandatoryPoiIds.Should().NotBeEmpty();
    }

    [Fact]
    public async Task GenerationService_ReturnsConflictingPoiName_WhenCspProvesInfeasibility()
    {
        var (input, matrix) = OptionalRouteOptimizationScenarios.CreateSyntheticCorpusScenario(10, 2, 53);
        var closed = input.Candidates.First(c => input.MandatoryPoiIds.Contains(c.Id));
        input = input with
        {
            Candidates = input.Candidates.Select(c => c.Id == closed.Id ? c with { OpeningHours = [] } : c).ToArray(),
        };
        var options = new SchedulingGenerationOptions { SolverMode = SchedulingSolverMode.Csp };
        var service = new ItineraryGenerationService(new StaticMatrixProvider(matrix), options);

        var result = await service.GenerateAsync(input, CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be(SchedulingErrorCodes.ConstraintsInfeasible);
        result.ErrorMessage.Should().Contain(closed.Name);
    }

    [Fact]
    public async Task GenerationService_UsesCsp_WhenSolverModeIsCsp()
    {
        var (input, matrix) = OptionalRouteOptimizationScenarios.CreateSyntheticCorpusScenario(20, 2, 54);
        var options = new SchedulingGenerationOptions { SolverMode = SchedulingSolverMode.Csp };
        var service = new ItineraryGenerationService(new StaticMatrixProvider(matrix), options);

        var result = await service.GenerateAsync(input, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Items
            .Where(item => item.Kind == ItineraryItemKind.Visit && item.IsMandatory)
            .Select(item => item.PointOfInterestId!.Value)
            .Should().BeEquivalentTo(input.MandatoryPoiIds);
    }

    [Fact]
    public async Task GenerationService_RecordsCspOutcome_WhenCspProducesThePlan()
    {
        var (input, matrix) = OptionalRouteOptimizationScenarios.CreateSyntheticCorpusScenario(20, 2, 54);
        var service = new ItineraryGenerationService(
            new StaticMatrixProvider(matrix), new SchedulingGenerationOptions { SolverMode = SchedulingSolverMode.Csp });

        var activity = await RecordSolverOutcome(() => service.GenerateAsync(input, CancellationToken.None));

        activity.GetTagItem("solver.mode").Should().Be(nameof(SchedulingSolverMode.Csp));
        activity.GetTagItem("solver.outcome").Should().Be(SchedulingSolverOutcomes.Csp);
        activity.GetTagItem("solver.kind").Should().Be(SchedulingSolverKinds.Csp);
        activity.GetTagItem("csp.pruned_by_bound").Should().BeOfType<int>();
        activity.GetTagItem("csp.nodes_expanded").Should().BeOfType<int>().Which.Should().BePositive();
    }

    /// <summary>
    /// Review PR #62 (Low 2): khi CSP không tìm được lịch tốt hơn và trả lại chính lịch heuristic, diagnostic phải nói rõ,
    /// để giám sát không tính lịch đó là do CSP tạo ra. c10-m0-s4 là scenario "equal" và c10-m0-s1 là scenario "better"
    /// của báo cáo benchmark.
    /// </summary>
    [Theory]
    [InlineData(4, true)]
    [InlineData(1, false)]
    public async Task GenerationService_TagsWhetherCspReturnedTheHeuristicPlan(int seed, bool returnedHeuristicPlan)
    {
        var (input, matrix) = OptionalRouteOptimizationScenarios.CreateSyntheticCorpusScenario(10, 0, seed);
        var heuristic = await new ItineraryGenerationService(
                new StaticMatrixProvider(matrix), new SchedulingGenerationOptions { SolverMode = SchedulingSolverMode.Heuristic })
            .GenerateAsync(input, CancellationToken.None);
        var service = new ItineraryGenerationService(
            new StaticMatrixProvider(matrix), new SchedulingGenerationOptions { SolverMode = SchedulingSolverMode.Csp });

        Result<GeneratedItineraryPlan>? result = null;
        var activity = await RecordSolverOutcome(async () => result = await service.GenerateAsync(input, CancellationToken.None));

        activity.GetTagItem("solver.outcome").Should().Be(SchedulingSolverOutcomes.Csp);
        activity.GetTagItem("csp.returned_heuristic_plan").Should().Be(returnedHeuristicPlan);
        (ProductRulePlanComparison.Compare(input, matrix, result!.Value, heuristic.Value) == 0).Should().Be(returnedHeuristicPlan);
    }

    /// <summary>
    /// R4: tắt tối ưu tuyến (<c>EnableOptionalRouteOptimization = false</c>) thì <c>SolverMode</c> bị bỏ qua và
    /// đường không tối ưu chạy như cũ, nên kế hoạch giống hệt chế độ Heuristic khi tắt tối ưu.
    /// </summary>
    [Theory]
    [InlineData(SchedulingSolverMode.Csp)]
    [InlineData(SchedulingSolverMode.MiniRouting)]
    public async Task GenerationService_IgnoresSolverMode_WhenOptimizationIsDisabled(SchedulingSolverMode mode)
    {
        var (input, matrix) = OptionalRouteOptimizationScenarios.CreateZigzagScenario();
        var unoptimized = await new ItineraryGenerationService(
                new StaticMatrixProvider(matrix),
                new SchedulingGenerationOptions { EnableOptionalRouteOptimization = false })
            .GenerateAsync(input, CancellationToken.None);
        var service = new ItineraryGenerationService(
            new StaticMatrixProvider(matrix),
            new SchedulingGenerationOptions { EnableOptionalRouteOptimization = false, SolverMode = mode });

        Result<GeneratedItineraryPlan>? result = null;
        var activity = await RecordSolverOutcome(async () => result = await service.GenerateAsync(input, CancellationToken.None));

        unoptimized.IsSuccess.Should().BeTrue();
        result!.IsSuccess.Should().BeTrue();
        result.Value.Should().BeEquivalentTo(unoptimized.Value);
        activity.GetTagItem("solver.outcome").Should().Be(SchedulingSolverOutcomes.Heuristic);
        activity.GetTagItem("solver.kind").Should().Be(SchedulingSolverKinds.Heuristic);
        activity.GetTagItem("csp.nodes_expanded").Should().BeNull();
    }

    /// <summary>R6: ghim chế độ Heuristic tường minh, để test không đổi nghĩa khi mặc định chuyển sang CSP (Phase 3).</summary>
    [Fact]
    public async Task GenerationService_RecordsHeuristicOutcome_InTheHeuristicMode()
    {
        var (input, matrix) = OptionalRouteOptimizationScenarios.CreateSyntheticCorpusScenario(20, 2, 54);
        var service = new ItineraryGenerationService(
            new StaticMatrixProvider(matrix), new SchedulingGenerationOptions { SolverMode = SchedulingSolverMode.Heuristic });

        var activity = await RecordSolverOutcome(() => service.GenerateAsync(input, CancellationToken.None));

        activity.GetTagItem("solver.mode").Should().Be(nameof(SchedulingSolverMode.Heuristic));
        activity.GetTagItem("solver.outcome").Should().Be(SchedulingSolverOutcomes.Heuristic);
        activity.GetTagItem("solver.kind").Should().Be(SchedulingSolverKinds.Heuristic);
        activity.GetTagItem("csp.nodes_expanded").Should().BeNull();
    }

    /// <summary>
    /// Lịch heuristic vượt MaxStops nên không được làm incumbent, và giới hạn một nút dừng CSP trước mọi lời giải: service
    /// trả lịch heuristic đã có và ghi đúng lý do chuyển sang nó.
    /// </summary>
    [Fact]
    public async Task GenerationService_RecordsSearchLimitFallback_WhenCspStopsBeforeAnySolution()
    {
        var (input, matrix) = OptionalRouteOptimizationScenarios.CreateSyntheticCorpusScenario(20, 2, 54);
        var options = new SchedulingGenerationOptions
        {
            SolverMode = SchedulingSolverMode.Csp,
            Csp = new CspOptions { MaxNodes = 1, MaxStops = 3 },
        };
        var service = new ItineraryGenerationService(new StaticMatrixProvider(matrix), options);

        Result<GeneratedItineraryPlan>? result = null;
        var activity = await RecordSolverOutcome(async () => result = await service.GenerateAsync(input, CancellationToken.None));

        result!.IsSuccess.Should().BeTrue();
        activity.GetTagItem("solver.outcome").Should().Be(SchedulingSolverOutcomes.HeuristicFallbackCspSearchLimit);
        activity.GetTagItem("solver.kind").Should().Be(SchedulingSolverKinds.CspFallback);
        activity.GetTagItem("csp.node_limit_reached").Should().Be(true);
        activity.GetTagItem("csp.solutions_found").Should().Be(0);
    }

    [Fact]
    public async Task GenerationService_RecordsInfeasibleOutcome_WhenNoSolverFindsAPlan()
    {
        var (input, matrix) = OptionalRouteOptimizationScenarios.CreateSyntheticCorpusScenario(10, 2, 53);
        var closed = input.Candidates.First(c => input.MandatoryPoiIds.Contains(c.Id));
        input = input with
        {
            Candidates = input.Candidates.Select(c => c.Id == closed.Id ? c with { OpeningHours = [] } : c).ToArray(),
        };
        var service = new ItineraryGenerationService(
            new StaticMatrixProvider(matrix), new SchedulingGenerationOptions { SolverMode = SchedulingSolverMode.Csp });

        var activity = await RecordSolverOutcome(() => service.GenerateAsync(input, CancellationToken.None));

        activity.GetTagItem("solver.outcome").Should().Be(SchedulingSolverOutcomes.Infeasible);
        activity.GetTagItem("solver.kind").Should().BeNull("no solver produced a plan");
    }

    /// <summary>
    /// R8 (mở rộng cho chế độ thử nghiệm): lịch do MiniRouting tạo được ghi là MiniRouting, không bị gộp vào Csp hay
    /// Heuristic.
    /// </summary>
    [Fact]
    public async Task GenerationService_RecordsTheMiniRoutingKind_WhenMiniRoutingProducesThePlan()
    {
        var (input, matrix) = OptionalRouteOptimizationScenarios.CreateSyntheticCorpusScenario(20, 2, 54);
        var service = new ItineraryGenerationService(
            new StaticMatrixProvider(matrix), new SchedulingGenerationOptions { SolverMode = SchedulingSolverMode.MiniRouting });

        var activity = await RecordSolverOutcome(() => service.GenerateAsync(input, CancellationToken.None));

        activity.GetTagItem("solver.outcome").Should().Be(SchedulingSolverOutcomes.MiniRouting);
        activity.GetTagItem("solver.kind").Should().Be(SchedulingSolverKinds.MiniRouting);
    }

    /// <summary>
    /// R8: diagnostic chỉ chứa số liệu tổng hợp, không bao giờ chứa danh tính người dùng hay POI. Mọi tag phải nằm trong
    /// danh sách cho phép, nên một tag mới mang Id hay tên sẽ làm test này fail; giá trị không được chứa tên POI.
    /// </summary>
    [Theory]
    [InlineData(SchedulingSolverMode.Heuristic, 2500)]
    [InlineData(SchedulingSolverMode.Csp, 2500)]
    [InlineData(SchedulingSolverMode.Csp, 0)]
    [InlineData(SchedulingSolverMode.MiniRouting, 2500)]
    public async Task GenerationService_RecordsOnlyAggregateSolverTags_WithoutUserOrPoiIdentity(SchedulingSolverMode mode, int timeLimitMilliseconds)
    {
        string[] allowed =
        [
            "solver.mode", "solver.outcome", "solver.kind",
            "csp.nodes_expanded", "csp.solutions_found", "csp.search_completed", "csp.node_limit_reached",
            "csp.time_limit_reached", "csp.used_relaxed_rest_model", "csp.chose_relaxed_rest_model", "csp.evaluator_calls",
            "csp.in_search_evaluation_limit_reached", "csp.tie_evaluation_limit_reached", "csp.returned_heuristic_plan",
            "csp.elapsed_ms", "csp.pruned_by_bound", "csp.pruned_by_constraints", "csp.pruned_by_forward_checking",
        ];
        var (input, matrix) = OptionalRouteOptimizationScenarios.CreateSyntheticCorpusScenario(20, 2, 54, RestPreference.Auto);
        var service = new ItineraryGenerationService(
            new StaticMatrixProvider(matrix),
            new SchedulingGenerationOptions { SolverMode = mode, Csp = new CspOptions { TimeLimitMilliseconds = timeLimitMilliseconds } });

        var activity = await RecordSolverOutcome(() => service.GenerateAsync(input, CancellationToken.None));

        activity.TagObjects.Select(tag => tag.Key).Should().BeSubsetOf(allowed);
        var names = input.Candidates.Select(candidate => candidate.Name).ToArray();
        activity.TagObjects
            .Select(tag => tag.Value?.ToString() ?? string.Empty)
            .Should().NotContain(value => names.Any(name => value.Contains(name, StringComparison.Ordinal)));
    }

    /// <summary>
    /// Hồi quy: CSP từng trừ sẵn thời gian dự phòng cho điểm nghỉ rồi kết luận vô nghiệm, trong khi lịch thật
    /// (có điểm nghỉ) vẫn vừa thời gian. Kết luận vô nghiệm giờ chỉ dựa trên mô hình nới lỏng.
    /// </summary>
    [Fact]
    public void Solve_DoesNotClaimInfeasibility_WhenOnlyTheRestReserveIsTight()
    {
        var (input, matrix) = RestReserveScenario();
        var (candidates, indices) = Index(input);

        var result = new CspItinerarySolver(new ItineraryScheduleEvaluator(Options), Options).Solve(input, matrix, candidates, indices);

        result.ProvedInfeasible.Should().BeFalse();
        result.Statistics.UsedRelaxedRestModel.Should().BeTrue();
        result.Schedule.Should().NotBeNull();
        result.Schedule!.Plan.Items.Count(item => item.Kind == ItineraryItemKind.Rest).Should().Be(1);
        result.Schedule.TotalDurationMinutes.Should().BeLessThanOrEqualTo(input.AvailableMinutes);
    }

    [Fact]
    public async Task GenerationService_FindsTheSchedule_WhenOnlyTheRestReserveIsTight()
    {
        var (input, matrix) = RestReserveScenario();
        var options = new SchedulingGenerationOptions { SolverMode = SchedulingSolverMode.Csp };

        var result = await new ItineraryGenerationService(new StaticMatrixProvider(matrix), options)
            .GenerateAsync(input, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
    }

    /// <summary>
    /// Dừng theo số nút (không theo đồng hồ) nên cùng một yêu cầu luôn cho cùng một lịch trình. Lưới an toàn thời gian
    /// được đặt rất xa: test kiểm tra tính tất định theo số nút, nên không được phụ thuộc tốc độ hay tải của máy chạy CI.
    /// </summary>
    [Theory]
    [InlineData(20, 2, 61)]
    [InlineData(40, 4, 62)]
    public void Solve_IsDeterministic_ForTheSameInput(int candidateCount, int mandatoryCount, int seed)
    {
        var (input, matrix) = OptionalRouteOptimizationScenarios.CreateSyntheticCorpusScenario(candidateCount, mandatoryCount, seed);
        var (candidates, indices) = Index(input);
        var nodeLimitedOnly = new CspOptions { TimeLimitMilliseconds = 600_000 };

        var first = new CspItinerarySolver(new ItineraryScheduleEvaluator(Options), Options, nodeLimitedOnly)
            .Solve(input, matrix, candidates, indices);
        var second = new CspItinerarySolver(new ItineraryScheduleEvaluator(Options), Options, nodeLimitedOnly)
            .Solve(input, matrix, candidates, indices);

        first.Statistics.TimeLimitReached.Should().BeFalse();
        first.Schedule.Should().NotBeNull();
        second.Schedule!.VisitPoiIds.Should().Equal(first.Schedule!.VisitPoiIds);
        second.Statistics.NodesExpanded.Should().Be(first.Statistics.NodesExpanded);
    }

    /// <summary>
    /// Hồi quy (review PR #57): một incumbent ngoài giới hạn có thể tốt hơn mọi lời giải hợp lệ, cắt mất chúng và trở thành
    /// kết quả, nên kết quả CSP từng vượt MaxStops hoặc chứa điểm ngoài top-N. Từ R1a, incumbent là lịch heuristic, và lịch
    /// đó chỉ được nhận khi nằm trong giới hạn.
    /// </summary>
    [Theory]
    [InlineData(20, 0, 71, true, false)]
    [InlineData(20, 1, 72, true, false)]
    [InlineData(40, 2, 73, true, false)]
    [InlineData(40, 2, 73, false, false)]
    [InlineData(20, 1, 72, true, true)]
    [InlineData(40, 2, 73, true, true)]
    public void Solve_RespectsMaxStopsAndOptionalDomain_UnderRestrictiveLimits(
        int candidateCount,
        int mandatoryCount,
        int seed,
        bool withHeuristicIncumbent,
        bool polish)
    {
        var (input, matrix) = OptionalRouteOptimizationScenarios.CreateSyntheticCorpusScenario(candidateCount, mandatoryCount, seed);
        var (candidates, indices) = Index(input);
        var maxStops = mandatoryCount + 2;
        var limits = new CspOptions { MaxStops = maxStops, MaxOptionalDomainSize = 3, PolishWithLocalSearch = polish };
        var heuristic = withHeuristicIncumbent ? HeuristicSchedule(input, matrix) : null;

        var result = new CspItinerarySolver(new ItineraryScheduleEvaluator(Options), Options, limits)
            .Solve(input, matrix, candidates, indices, heuristic);

        result.Schedule.Should().NotBeNull();
        result.Schedule!.VisitPoiIds.Count.Should().BeLessThanOrEqualTo(maxStops);
        input.MandatoryPoiIds.Should().BeSubsetOf(result.Schedule.VisitPoiIds);
        result.Schedule.VisitPoiIds
            .Where(id => !input.MandatoryPoiIds.Contains(id))
            .Should().BeSubsetOf(TopRankedOptionalIds(input, matrix, 3));
        if (heuristic is not null && heuristic.VisitPoiIds.Count > maxStops)
        {
            result.Statistics.InitialIncumbentAccepted.Should().BeFalse();
        }
    }

    /// <summary>
    /// R1a: incumbent chỉ là cận để cắt nhánh sớm. Khi phép duyệt chạy hết cây, có hay không có lịch heuristic làm
    /// incumbent đều phải cho đúng lịch tối ưu của phép duyệt vét cạn, trong cùng giới hạn.
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    public void Solve_HeuristicIncumbentDoesNotChangeTheOptimum(int seed)
    {
        var (input, matrix) = SmallScenario(seed);
        var (candidates, indices) = Index(input);
        var ctx = RoutingContext.Create(input, matrix, candidates, indices, Options, Options.MiniRouting);
        var exhaustive = CspProductRuleOracle.Best(input, matrix, Options, ctx);
        var heuristic = HeuristicSchedule(input, matrix);
        var comparer = CspProductRuleOracle.Comparer(input, matrix);

        var withIncumbent = new CspItinerarySolver(new ItineraryScheduleEvaluator(Options), Options, Exhaustive)
            .Solve(input, matrix, candidates, indices, heuristic);
        var withoutIncumbent = new CspItinerarySolver(new ItineraryScheduleEvaluator(Options), Options, Exhaustive)
            .Solve(input, matrix, candidates, indices);

        withIncumbent.Statistics.SearchCompleted.Should().BeTrue();
        withIncumbent.Statistics.InitialIncumbentAccepted.Should().Be(heuristic is not null);
        if (exhaustive is null)
        {
            withIncumbent.Schedule.Should().BeNull();
            withoutIncumbent.Schedule.Should().BeNull();
            return;
        }

        comparer.Compare(withIncumbent.Schedule, exhaustive).Should().Be(0);
        comparer.Compare(withoutIncumbent.Schedule, exhaustive).Should().Be(0);
    }

    /// <summary>
    /// R1a: lịch heuristic là incumbent ban đầu và là một ứng viên ở bước chọn cuối, nên kết quả CSP không bao giờ kém nó
    /// theo product rule, kể cả khi giới hạn số nút dừng phép duyệt gần như ngay lập tức.
    /// </summary>
    [Theory]
    [MemberData(nameof(NeverWorseScenarios))]
    public void Solve_IsNeverWorseThanTheHeuristic_EvenWithATinyNodeBudget(string scenario, int maxNodes)
    {
        var (input, matrix) = NeverWorseScenario(scenario);
        var (candidates, indices) = Index(input);
        var heuristic = HeuristicSchedule(input, matrix);

        // Chỉ node budget được dừng phép duyệt: giới hạn thời gian mặc định 2,5 giây có thể chạm trên máy CI tải nặng, và
        // khi đó R5 cố ý bỏ kết quả CSP, đã có test riêng ở Solve_DiscardsThePartialResult_WhenTheWallClockSafetyLimitFires.
        var nodeLimitedOnly = new CspOptions { MaxNodes = maxNodes, TimeLimitMilliseconds = 600_000 };
        var result = new CspItinerarySolver(new ItineraryScheduleEvaluator(Options), Options, nodeLimitedOnly)
            .Solve(input, matrix, candidates, indices, heuristic);

        heuristic.Should().NotBeNull();
        result.Statistics.InitialIncumbentAccepted.Should().BeTrue();
        result.Schedule.Should().NotBeNull();
        CspProductRuleOracle.Comparer(input, matrix).Compare(result.Schedule, heuristic).Should().BeLessThanOrEqualTo(0);
        var validate = () => new GeneratedItineraryInvariantValidator(Options).Validate(input, matrix, candidates, result.Schedule!.Plan);
        validate.Should().NotThrow();
    }

    public static TheoryData<string, int> NeverWorseScenarios()
    {
        var data = new TheoryData<string, int>();
        foreach (var scenario in NeverWorseScenarioNames)
        {
            data.Add(scenario, 1);
            data.Add(scenario, 200_000);
        }

        return data;
    }

    /// <summary>
    /// R2, R3: không có giới hạn cứng số điểm dừng, và miền giá trị gồm mọi điểm tùy chọn mà heuristic cũng xét.
    /// </summary>
    [Fact]
    public void CspOptions_HaveNoStopCapAndTheFullDomain_ByDefault()
    {
        var options = new CspOptions();

        options.MaxStops.Should().BeNull();
        options.MaxOptionalDomainSize.Should().BeNull();
    }

    /// <summary>R5 và Report 3 V2: lưới an toàn thời gian là 2.500 ms; số nút vẫn là điều kiện dừng chính.</summary>
    [Fact]
    public void CspOptions_UseTheReport3SafetyLimit_ByDefault()
    {
        new CspOptions().TimeLimitMilliseconds.Should().Be(2500);
    }

    /// <summary>
    /// R5: khi lưới an toàn thời gian bị chạm, kết quả CSP dở dang phụ thuộc tốc độ máy nên bị bỏ hẳn. Giới hạn 0 ms
    /// làm lưới chạm ở lần kiểm tra đồng hồ đầu tiên, nên test không phụ thuộc tốc độ máy.
    /// </summary>
    [Fact]
    public void Solve_DiscardsThePartialResult_WhenTheWallClockSafetyLimitFires()
    {
        var (input, matrix) = OptionalRouteOptimizationScenarios.CreateSyntheticCorpusScenario(40, 0, 1);
        var (candidates, indices) = Index(input);

        var result = new CspItinerarySolver(new ItineraryScheduleEvaluator(Options), Options, new CspOptions { TimeLimitMilliseconds = 0 })
            .Solve(input, matrix, candidates, indices, HeuristicSchedule(input, matrix));

        result.Statistics.TimeLimitReached.Should().BeTrue();
        result.Statistics.SearchCompleted.Should().BeFalse();
        result.Schedule.Should().BeNull();
        result.ProvedInfeasible.Should().BeFalse();
    }

    /// <summary>
    /// Review Phase 3 (blocker 2): mốc thời gian là mốc cứng, kể cả khi cây tìm kiếm dừng trước lần xem đồng hồ đầu tiên
    /// của cách cũ (mỗi 256 nút): giới hạn số nút nhỏ, hoặc bài toán nhỏ duyệt hết cây sớm.
    /// </summary>
    [Theory]
    [InlineData(40, 100)]
    [InlineData(3, 200_000)]
    public void Solve_HonorsTheTimeLimit_WhenTheSearchStopsBeforeTheOldClockCheckInterval(int candidateCount, int maxNodes)
    {
        var (input, matrix) = OptionalRouteOptimizationScenarios.CreateSyntheticCorpusScenario(candidateCount, 0, 1);
        var (candidates, indices) = Index(input);

        var result = new CspItinerarySolver(
                new ItineraryScheduleEvaluator(Options), Options, new CspOptions { MaxNodes = maxNodes, TimeLimitMilliseconds = 0 })
            .Solve(input, matrix, candidates, indices, HeuristicSchedule(input, matrix));

        result.Statistics.NodesExpanded.Should().BeLessThan(256);
        result.Statistics.TimeLimitReached.Should().BeTrue();
        result.Schedule.Should().BeNull();
    }

    /// <summary>
    /// Review Phase 3 (blocker 2): thời gian của evaluator trong lúc duyệt (khi có điểm nghỉ) tính vào cùng một mốc. Mỗi
    /// lần chấm chậm 60 ms với mốc 100 ms: phải dừng ngay sau lần chấm làm vượt mốc, không chạy tiếp tới lần xem đồng hồ
    /// của bước duyệt.
    /// </summary>
    [Fact]
    public void Solve_StopsRightAfterTheEvaluationThatCrossesTheTimeLimit_DuringTheSearch()
    {
        var (input, matrix) = OptionalRouteOptimizationScenarios.CreateSyntheticCorpusScenario(20, 2, 54, RestPreference.Frequent);
        var (candidates, indices) = Index(input);
        var solver = new CspItinerarySolver(new ItineraryScheduleEvaluator(Options), Options, new CspOptions { TimeLimitMilliseconds = 100 })
        {
            BeforeEvaluation = _ => Thread.Sleep(60),
        };

        var result = solver.Solve(input, matrix, candidates, indices, HeuristicSchedule(input, matrix));

        result.Statistics.TimeLimitReached.Should().BeTrue();
        result.Schedule.Should().BeNull();
        result.Statistics.EvaluatorCalls.Should().BeLessThanOrEqualTo(2);
    }

    /// <summary>
    /// Review Phase 3 (blocker 2): bước chấm các lời giải tốt nhất sau khi duyệt xong cũng nằm trong mốc. Không có điểm
    /// nghỉ thì evaluator chỉ chạy ở bước này; một lần chấm 300 ms với mốc 200 ms phải làm kết quả CSP bị bỏ.
    /// </summary>
    [Fact]
    public void Solve_DiscardsTheResult_WhenEvaluationAfterTheSearchCrossesTheTimeLimit()
    {
        var (input, matrix) = OptionalRouteOptimizationScenarios.CreateSyntheticCorpusScenario(8, 0, 1);
        var (candidates, indices) = Index(input);
        var solver = new CspItinerarySolver(new ItineraryScheduleEvaluator(Options), Options, new CspOptions { TimeLimitMilliseconds = 200 })
        {
            BeforeEvaluation = _ => Thread.Sleep(300),
        };

        var result = solver.Solve(input, matrix, candidates, indices, HeuristicSchedule(input, matrix));

        result.Statistics.TimeLimitReached.Should().BeTrue();
        result.Schedule.Should().BeNull();
    }

    /// <summary>
    /// Review Phase 3 (blocker 2): evaluator nhận token của mốc thời gian, nên một lần chấm đang chạy dừng ngay khi tới mốc
    /// thay vì chạy hết (ở đây là chờ tới 3 giây).
    /// </summary>
    [Fact]
    public void Solve_InterruptsARunningEvaluation_AtTheTimeLimit()
    {
        var (input, matrix) = OptionalRouteOptimizationScenarios.CreateSyntheticCorpusScenario(8, 0, 1);
        var (candidates, indices) = Index(input);
        var solver = new CspItinerarySolver(new ItineraryScheduleEvaluator(Options), Options, new CspOptions { TimeLimitMilliseconds = 200 })
        {
            BeforeEvaluation = token =>
            {
                token.WaitHandle.WaitOne(TimeSpan.FromSeconds(3));
                token.ThrowIfCancellationRequested();
            },
        };

        var watch = Stopwatch.StartNew();
        var result = solver.Solve(input, matrix, candidates, indices, HeuristicSchedule(input, matrix));
        watch.Stop();

        result.Statistics.TimeLimitReached.Should().BeTrue();
        result.Schedule.Should().BeNull();
        watch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(2));
    }

    /// <summary>
    /// Review Phase 3 (blocker 2): khi bật polish, tìm kiếm cục bộ chạm mốc thì kết quả cũng bị bỏ như ở bước duyệt, vì
    /// lời giải đã polish dở dang phụ thuộc tốc độ máy.
    /// </summary>
    [Fact]
    public void Solve_DiscardsThePolishedResult_WhenLocalSearchReachesTheTimeLimit()
    {
        var options = new SchedulingGenerationOptions { MiniRouting = new MiniRoutingOptions { MaxGlsIterations = int.MaxValue } };
        var (input, matrix) = OptionalRouteOptimizationScenarios.CreateSyntheticCorpusScenario(12, 0, 1);
        var (candidates, indices) = Index(input);

        var result = new CspItinerarySolver(
                new ItineraryScheduleEvaluator(options),
                options,
                new CspOptions { PolishWithLocalSearch = true, TimeLimitMilliseconds = 150 })
            .Solve(input, matrix, candidates, indices, HeuristicSchedule(input, matrix));

        result.Statistics.TimeLimitReached.Should().BeTrue();
        result.Schedule.Should().BeNull();
    }

    /// <summary>
    /// Review Phase 3 (blocker 2): hủy từ bên gọi (request bị hủy) vẫn ném lỗi hủy, không bị coi là chạm mốc thời gian.
    /// </summary>
    [Fact]
    public void Solve_PropagatesCallerCancellation_DuringAnEvaluation()
    {
        var (input, matrix) = OptionalRouteOptimizationScenarios.CreateSyntheticCorpusScenario(8, 0, 1);
        var (candidates, indices) = Index(input);
        using var caller = new CancellationTokenSource();
        var solver = new CspItinerarySolver(new ItineraryScheduleEvaluator(Options), Options, new CspOptions { TimeLimitMilliseconds = 600_000 })
        {
            BeforeEvaluation = _ => caller.Cancel(),
        };

        var act = () => solver.Solve(input, matrix, candidates, indices, HeuristicSchedule(input, matrix), caller.Token);

        act.Should().Throw<OperationCanceledException>();
    }

    /// <summary>
    /// Review Phase 3 (blocker 2): request đã bị hủy trước khi giải thì không trả kết quả nào, kể cả với cây nhỏ.
    /// </summary>
    [Fact]
    public void Solve_Throws_WhenTheCallerIsAlreadyCancelled_EvenForASmallTree()
    {
        var (input, matrix) = OptionalRouteOptimizationScenarios.CreateSyntheticCorpusScenario(3, 0, 1);
        var (candidates, indices) = Index(input);
        using var caller = new CancellationTokenSource();
        caller.Cancel();

        var act = () => new CspItinerarySolver(
                new ItineraryScheduleEvaluator(Options), Options, new CspOptions { MaxNodes = 100, TimeLimitMilliseconds = 600_000 })
            .Solve(input, matrix, candidates, indices, HeuristicSchedule(input, matrix), caller.Token);

        act.Should().Throw<OperationCanceledException>();
    }

    /// <summary>
    /// R5: khi lưới an toàn thời gian bị chạm, service trả đúng lịch heuristic (đã tính sẵn cho R1a), không dùng kết quả
    /// CSP dở dang, và ghi lại lý do chuyển sang heuristic. Nhờ vậy kết quả không phụ thuộc vào lúc dừng.
    /// </summary>
    [Fact]
    public async Task GenerationService_ReturnsTheHeuristicPlanExactly_WhenTheWallClockSafetyLimitFires()
    {
        var (input, matrix) = OptionalRouteOptimizationScenarios.CreateSyntheticCorpusScenario(40, 0, 1);
        var heuristic = await new ItineraryGenerationService(new StaticMatrixProvider(matrix))
            .GenerateAsync(input, CancellationToken.None);
        var service = new ItineraryGenerationService(
            new StaticMatrixProvider(matrix),
            new SchedulingGenerationOptions { SolverMode = SchedulingSolverMode.Csp, Csp = new CspOptions { TimeLimitMilliseconds = 0 } });

        Result<GeneratedItineraryPlan>? result = null;
        var activity = await RecordSolverOutcome(async () => result = await service.GenerateAsync(input, CancellationToken.None));

        result!.IsSuccess.Should().BeTrue();
        result.Value.Should().BeEquivalentTo(heuristic.Value);
        activity.GetTagItem("solver.outcome").Should().Be(SchedulingSolverOutcomes.HeuristicFallbackCspTimeLimit);
        activity.GetTagItem("solver.kind").Should().Be(SchedulingSolverKinds.CspFallback, "acceptance criterion 7");
        activity.GetTagItem("csp.time_limit_reached").Should().Be(true);
    }

    /// <summary>
    /// R2: ở các scenario này heuristic xếp 11 điểm, còn CSP với giới hạn cũ 10 điểm dừng chỉ xếp được 10 (benchmark
    /// 2026-10-11). Không còn giới hạn cứng thì CSP xếp được ít nhất bằng heuristic.
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    [InlineData(5)]
    public void Solve_SchedulesMoreThanTenStops_WhenTheHeuristicDoes(int seed)
    {
        var (input, matrix) = OptionalRouteOptimizationScenarios.CreateSyntheticCorpusScenario(20, 0, seed);
        var (candidates, indices) = Index(input);
        var heuristic = HeuristicSchedule(input, matrix);
        var nodeLimitedOnly = new CspOptions { TimeLimitMilliseconds = 600_000 };

        var result = new CspItinerarySolver(new ItineraryScheduleEvaluator(Options), Options, nodeLimitedOnly)
            .Solve(input, matrix, candidates, indices, heuristic);

        heuristic!.VisitPoiIds.Count.Should().Be(11);
        result.Schedule!.VisitPoiIds.Count.Should().BeGreaterThanOrEqualTo(11);
        CspProductRuleOracle.Comparer(input, matrix).Compare(result.Schedule, heuristic).Should().BeLessThanOrEqualTo(0);
    }

    /// <summary>
    /// TM-216 dưới CSP (P2.3c): AI chỉ ảnh hưởng thứ tự ưu tiên, mọi ràng buộc cứng vẫn do bộ lập lịch quyết định. Điểm
    /// tùy chọn có điểm AI cao nhất nhưng chỉ mở cửa sau khi lịch đã phải kết thúc thì vẫn bị loại, và lịch vẫn hợp lệ.
    /// </summary>
    [Fact]
    public async Task GenerationService_ExcludesTheTopAiScoredOptional_WhenItIsTimeInfeasible()
    {
        var (input, matrix) = OptionalRouteOptimizationScenarios.CreateSyntheticCorpusScenario(10, 1, 41);
        var top = input.Candidates
            .Where(candidate => !input.MandatoryPoiIds.Contains(candidate.Id))
            .MaxBy(candidate => candidate.EffectiveDesirabilityScore)!;
        input = input with
        {
            Candidates = input.Candidates
                .Select(candidate => candidate.Id == top.Id
                    ? candidate with
                    {
                        EffectiveDesirabilityScore = 1m,
                        OpeningHours = [new GenerationOpeningHours(2, new TimeOnly(23, 0), new TimeOnly(23, 59))],
                    }
                    : candidate)
                .ToArray(),
        };
        var service = new ItineraryGenerationService(
            new StaticMatrixProvider(matrix), new SchedulingGenerationOptions { SolverMode = SchedulingSolverMode.Csp });

        var result = await service.GenerateAsync(input, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Items.Should().NotContain(item => item.PointOfInterestId == top.Id);
        var (candidates, _) = Index(input);
        var validate = () => new GeneratedItineraryInvariantValidator(Options).Validate(input, matrix, candidates, result.Value);
        validate.Should().NotThrow();
    }

    /// <summary>
    /// R1: điểm tùy chọn xếp hạng cao hơn thắng mọi tổ hợp điểm xếp hạng thấp hơn, dù tốn nhiều phút di chuyển hơn.
    /// Chỉ chọn được hoặc H (xa, xếp hạng cao nhất) hoặc cả L1 và L2 (gần). Mục tiêu cũ "phút di chuyển + phạt bỏ điểm"
    /// chọn L1 và L2 (15 + 150 = 165 so với 120 + 150 = 270); product rule chọn H.
    /// </summary>
    [Fact]
    public void Solve_NeverTradesAHigherRankedOptionalForTravelTime()
    {
        var candidates = new[]
        {
            OptionalRouteOptimizationScenarios.CreateCandidate(1, "H", 30, 10_000m, 0.9m),
            OptionalRouteOptimizationScenarios.CreateCandidate(2, "L1", 30, 10_000m, 0.5m),
            OptionalRouteOptimizationScenarios.CreateCandidate(3, "L2", 30, 10_000m, 0.4m),
        };
        var input = OptionalRouteOptimizationScenarios.CreateInput(180, candidates, []);

        // H một mình: 60+10 → 70, thăm → 100, về 60+15 → 175 ≤ 180. L1, L2: 15, 45, 60, 90, về 20 → 110.
        // H cùng một điểm L bất kỳ: ít nhất 220 > 180 phút.
        var matrix = RouteDurationMatrix.Create(new[,]
        {
            /* Start */ { 0, 60, 5, 5, 0 },
            /* H     */ { 60, 0, 60, 60, 60 },
            /* L1    */ { 5, 60, 0, 5, 5 },
            /* L2    */ { 5, 60, 5, 0, 5 },
            /* End   */ { 0, 60, 5, 5, 0 },
        });
        var (matrixCandidates, indices) = Index(input);

        var result = new CspItinerarySolver(new ItineraryScheduleEvaluator(Options), Options)
            .Solve(input, matrix, matrixCandidates, indices);

        result.Schedule.Should().NotBeNull();
        result.Schedule!.VisitPoiIds.Should().Equal(1L);
    }

    /// <summary>
    /// R1: thứ hạng chuẩn đã gồm các quy tắc phá hòa của ranking. Hai điểm cùng điểm AI, chỉ đặt được một; điểm có
    /// scenic score cao hơn xếp trước nên được chọn, dù điểm kia gần hơn nhiều.
    /// </summary>
    [Fact]
    public void Solve_FollowsTheCanonicalRankingTieBreak()
    {
        var candidates = new[]
        {
            OptionalRouteOptimizationScenarios.CreateCandidate(1, "Scenic", 30, 10_000m, 0.8m, scenicScoreForRanking: 0.9m),
            OptionalRouteOptimizationScenarios.CreateCandidate(2, "Near", 30, 10_000m, 0.8m, scenicScoreForRanking: 0.5m),
        };
        var input = OptionalRouteOptimizationScenarios.CreateInput(150, candidates, []);

        // Scenic một mình: 40+10 → 50, thăm → 80, về 40+15 → 135 ≤ 150. Near một mình: 65. Cả hai: ít nhất 160 > 150.
        var matrix = RouteDurationMatrix.Create(new[,]
        {
            /* Start  */ { 0, 40, 5, 0 },
            /* Scenic */ { 40, 0, 40, 40 },
            /* Near   */ { 5, 40, 0, 5 },
            /* End    */ { 0, 40, 5, 0 },
        });
        var (matrixCandidates, indices) = Index(input);

        var result = new CspItinerarySolver(new ItineraryScheduleEvaluator(Options), Options)
            .Solve(input, matrix, matrixCandidates, indices);

        result.Schedule.Should().NotBeNull();
        result.Schedule!.VisitPoiIds.Should().Equal(1L);
    }

    /// <summary>
    /// Hồi quy: ma trận đường đi thật không thỏa bất đẳng thức tam giác. Từ điểm bắt buộc về thẳng đích bị trễ,
    /// nhưng về qua một điểm tùy chọn nằm trên đường tắt thì kịp. CSP từng cắt nhánh ngay khi tiền tố không về
    /// thẳng đích kịp, nên bỏ sót lời giải duy nhất và kết luận sai là vô nghiệm.
    /// </summary>
    [Fact]
    public void Solve_FindsTheRoute_WhenOnlyAShortcutThroughAnotherStopReachesTheEndInTime()
    {
        // Buffer mặc định: +10 phút mỗi chặng, +15 phút chặng về đích.
        // Bắt buộc rồi về thẳng: 10+10 → 20, thăm 30 → 50, về 100+15 → 165 > 120 phút.
        // Bắt buộc → đường tắt: 50 + 5+10 → 65, thăm 15 → 80, về 5+15 → 100 ≤ 120 phút.
        var mandatory = OptionalRouteOptimizationScenarios.CreateCandidate(1, "Mandatory", 30, 10_000m, 0.5m);
        var shortcut = OptionalRouteOptimizationScenarios.CreateCandidate(2, "Shortcut", 15, 10_000m, 0.9m);
        var input = OptionalRouteOptimizationScenarios.CreateInput(
            availableMinutes: 120,
            candidates: [mandatory, shortcut],
            mandatoryPoiIds: [1]);
        var matrix = RouteDurationMatrix.Create(new int[,]
        {
            { 0, 10, 50, 100 },
            { 10, 0, 5, 100 },
            { 50, 50, 0, 5 },
            { 100, 100, 5, 0 },
        });
        var (candidates, indices) = Index(input);

        var result = new CspItinerarySolver(new ItineraryScheduleEvaluator(Options), Options).Solve(input, matrix, candidates, indices);

        result.ProvedInfeasible.Should().BeFalse();
        result.Schedule.Should().NotBeNull();
        result.Schedule!.VisitPoiIds.Should().Equal(1L, 2L);
    }

    /// <summary>
    /// Hai điểm bắt buộc A và B đều về đích rất chậm (200 phút), còn điểm tùy chọn C gần mọi điểm và cách đích 5 phút.
    /// [A, B] không về kịp, [A, B, C] thì kịp, nên C nằm trên lộ trình về duy nhất. Phép duyệt loại tiền tố không về thẳng
    /// đích kịp sẽ kết luận nhầm là vô nghiệm. (Đề xuất trong review PR #57 ở head 29a2989; trên 94995d9 lịch trả về null.)
    /// </summary>
    [Fact]
    public void Solve_FindsTheRouteThatOnlyReachesTheEndThroughAnOptionalPoi()
    {
        var candidates = new[]
        {
            OptionalRouteOptimizationScenarios.CreateCandidate(1, "A", 30, 10_000m, 0.5m),
            OptionalRouteOptimizationScenarios.CreateCandidate(2, "B", 30, 10_000m, 0.5m),
            OptionalRouteOptimizationScenarios.CreateCandidate(3, "C", 30, 10_000m, 0.9m),
        };
        var input = OptionalRouteOptimizationScenarios.CreateInput(180, candidates, [1, 2], RestPreference.None);
        var matrix = RouteDurationMatrix.Create(new[,]
        {
            /* Start */ { 0, 5, 5, 5, 60 },
            /* A     */ { 5, 0, 5, 5, 200 },
            /* B     */ { 5, 5, 0, 5, 200 },
            /* C     */ { 5, 5, 5, 0, 5 },
            /* End   */ { 60, 60, 60, 60, 0 },
        });
        var options = new SchedulingGenerationOptions();
        var indices = new Dictionary<long, int> { [1] = 1, [2] = 2, [3] = 3 };

        var result = new CspItinerarySolver(new ItineraryScheduleEvaluator(options), options, new CspOptions())
            .Solve(input, matrix, candidates, indices);

        result.Schedule.Should().NotBeNull();
        result.Schedule!.VisitPoiIds.Last().Should().Be(3);
        result.ProvedInfeasible.Should().BeFalse();
    }

    /// <summary>
    /// Số liệu khớp đúng giới hạn: budget được kiểm tra trước khi đếm nút, nên nút bị từ chối không được tính
    /// và <c>NodesExpanded</c> không bao giờ vượt <c>MaxNodes</c>.
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(300)]
    public void Solve_CountsExactlyMaxNodes_WhenTheNodeLimitStopsTheSearch(int maxNodes)
    {
        var (input, matrix) = OptionalRouteOptimizationScenarios.CreateSyntheticCorpusScenario(40, 4, 62);
        var (candidates, indices) = Index(input);

        var result = new CspItinerarySolver(new ItineraryScheduleEvaluator(Options), Options, new CspOptions { MaxNodes = maxNodes })
            .Solve(input, matrix, candidates, indices);

        result.Statistics.NodeLimitReached.Should().BeTrue();
        result.Statistics.SearchCompleted.Should().BeFalse();
        result.Statistics.NodesExpanded.Should().Be(maxNodes);
    }

    [Fact]
    public void Solve_StopsPromptly_WhenCancelled()
    {
        var (input, matrix) = OptionalRouteOptimizationScenarios.CreateSyntheticCorpusScenario(40, 4, 63);
        var (candidates, indices) = Index(input);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        var solve = () => new CspItinerarySolver(new ItineraryScheduleEvaluator(Options), Options)
            .Solve(input, matrix, candidates, indices, cancelled.Token);

        solve.Should().Throw<OperationCanceledException>();
    }

    private static (GenerationInput Input, RouteDurationMatrix Matrix) RestReserveScenario()
    {
        // Một điểm bắt buộc 150 phút, nghỉ thường xuyên, 227 phút: đi 20 + thăm 150 + nghỉ 30 + về 20 = 220 phút.
        // Mô hình có dự phòng nghỉ (40 phút) chỉ còn 187 phút nên tưởng là vô nghiệm.
        var candidate = OptionalRouteOptimizationScenarios.CreateCandidate(1, "Bà Nà Hills", 150, 0m, 0.9m);
        var input = OptionalRouteOptimizationScenarios.CreateInput(
            availableMinutes: 227,
            candidates: [candidate],
            mandatoryPoiIds: [1],
            restPreference: RestPreference.Frequent);
        var matrix = RouteDurationMatrix.Create(new int[,]
        {
            { 0, 10, 10 },
            { 10, 0, 5 },
            { 10, 5, 0 },
        });
        return (input, matrix);
    }

    private static readonly string[] NeverWorseScenarioNames =
    [
        "zigzag", "tail-infeasible", "asymmetric", "two-opt", "relocate", "reconsideration",
        "c20-m0-s3", "c20-m3-s4", "c40-m1-s2", "c40-m6-s5", "c20-m1-s7-frequent",
    ];

    private static (GenerationInput Input, RouteDurationMatrix Matrix) NeverWorseScenario(string name)
    {
        if (SolverComparisonCorpus.NamedFixtures.FirstOrDefault(fixture => fixture.Name == name) is { Create: { } create })
        {
            return create();
        }

        // Dạng c{ứng viên}-m{bắt buộc}-s{seed}[-frequent].
        var parts = name.Split('-');
        var rest = parts.Length > 3 ? Enum.Parse<RestPreference>(parts[3], ignoreCase: true) : RestPreference.None;
        return OptionalRouteOptimizationScenarios.CreateSyntheticCorpusScenario(
            int.Parse(parts[0][1..]), int.Parse(parts[1][1..]), int.Parse(parts[2][1..]), rest);
    }

    /// <summary>Lịch của heuristic (<see cref="OptionalRouteOptimizer"/>) cho cùng đầu vào, như service truyền cho CSP.</summary>
    private static EvaluatedItinerarySchedule? HeuristicSchedule(GenerationInput input, RouteDurationMatrix matrix)
    {
        var (candidates, indices) = Index(input);
        var mandatory = input.MandatoryPoiIds.Select(id => candidates.Single(c => c.Id == id)).ToArray();
        return new OptionalRouteOptimizer(new ItineraryScheduleEvaluator(Options), Options)
            .Optimize(input, mandatory, matrix, candidates, indices)?.Schedule;
    }

    private static (GenerationInput Input, RouteDurationMatrix Matrix) SmallScenario(int seed)
    {
        var random = new Random(seed);
        var (input, matrix) = OptionalRouteOptimizationScenarios.CreateSyntheticCorpusScenario(6 + (seed % 3), seed % 4, seed);
        var tight = seed % 2 == 0;
        return (input with
        {
            AvailableMinutes = random.Next(2, 7) * 60,
            BudgetVnd = random.Next(3, 9) * 15_000m,
            Candidates = input.Candidates
                .Select(c => !tight
                    ? c
                    : c with
                    {
                        OpeningHours =
                        [
                            new GenerationOpeningHours(2, new TimeOnly(random.Next(8, 12), 0), new TimeOnly(random.Next(10, 15), 0)),
                        ],
                    })
                .ToArray(),
        }, matrix);
    }

    private static (GenerationInput Input, RouteDurationMatrix Matrix) TightScenario(int seed)
    {
        var (input, matrix) = OptionalRouteOptimizationScenarios.CreateSyntheticCorpusScenario(30, 3, seed);
        var random = new Random(seed * 97);
        var mandatory = input.MandatoryPoiIds.ToHashSet();
        return (input with
        {
            Candidates = input.Candidates
                .Select(c =>
                {
                    if (mandatory.Contains(c.Id))
                    {
                        return c;
                    }

                    var open = random.Next(8, 15);
                    var close = Math.Min(22, open + random.Next(1, 4));
                    return c with { OpeningHours = [new GenerationOpeningHours(2, new TimeOnly(open, 0), new TimeOnly(close, 0))] };
                })
                .ToArray(),
        }, matrix);
    }

    /// <summary>
    /// Bắt activity kết quả bộ giải của đúng lần sinh này: listener là toàn cục và test chạy song song,
    /// nên chỉ nhận activity cùng TraceId với activity cha của test.
    /// </summary>
    private static async Task<Activity> RecordSolverOutcome(Func<Task> generate)
    {
        using var parent = new Activity(nameof(RecordSolverOutcome)).Start();
        var traceId = parent.TraceId;
        Activity? recorded = null;
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == SchedulingSolverDiagnostics.ActivitySourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> options) => options.TraceId == traceId
                ? ActivitySamplingResult.AllDataAndRecorded
                : ActivitySamplingResult.None,
            ActivityStopped = activity =>
            {
                if (activity.TraceId == traceId && activity.OperationName == SchedulingSolverDiagnostics.OutcomeActivityName)
                {
                    recorded = activity;
                }
            },
        };
        ActivitySource.AddActivityListener(listener);

        await generate();

        recorded.Should().NotBeNull();
        return recorded!;
    }

    /// <summary>Id của <paramref name="count"/> điểm tùy chọn có thứ hạng chuẩn cao nhất: miền giá trị của CSP.</summary>
    private static long[] TopRankedOptionalIds(GenerationInput input, RouteDurationMatrix matrix, int count)
    {
        var (candidates, indices) = Index(input);
        var ctx = RoutingContext.Create(input, matrix, candidates, indices, Options, Options.MiniRouting);
        return CspProductRuleOracle.TopRankedOptionalNodes(input, matrix, ctx, count)
            .Select(node => ctx.Nodes[node].Candidate.Id)
            .ToArray();
    }

    private static (GenerationCandidate[] Candidates, Dictionary<long, int> Indices) Index(GenerationInput input)
    {
        var candidates = input.Candidates.ToArray();
        return (candidates, candidates.Select((c, i) => (c.Id, Index: i + 1)).ToDictionary(x => x.Id, x => x.Index));
    }

    private sealed class StaticMatrixProvider(RouteDurationMatrix matrix) : IRouteDurationProvider
    {
        public Task<RouteDurationMatrix> GetMatrixAsync(
            IReadOnlyList<RoutePoint> points,
            TransportMode transportMode,
            CancellationToken cancellationToken) => Task.FromResult(matrix);
    }
}