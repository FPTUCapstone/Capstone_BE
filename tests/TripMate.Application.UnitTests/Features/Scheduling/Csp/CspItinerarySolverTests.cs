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
    /// Forward checking và branch and bound không được bỏ sót lời giải: trên bài nhỏ, CSP phải cho đúng
    /// mục tiêu tối ưu của phép duyệt vét cạn không cắt tỉa, hoặc cùng kết luận vô nghiệm.
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
        var exhaustive = ExhaustiveBest(ctx);

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
            result.Statistics.BestObjective.Should().Be(exhaustive.Value);
        }
    }

    [Theory]
    [InlineData(20, 2, 41)]
    [InlineData(40, 4, 42)]
    public void Solve_ReturnsValidScheduleWithAllMandatoryPois(int candidateCount, int mandatoryCount, int seed)
    {
        var (input, matrix) = OptionalRouteOptimizationScenarios.CreateSyntheticCorpusScenario(candidateCount, mandatoryCount, seed);
        var (candidates, indices) = Index(input);

        var result = new CspItinerarySolver(new ItineraryScheduleEvaluator(Options), Options).Solve(input, matrix, candidates, indices);

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
        activity.GetTagItem("csp.nodes_expanded").Should().BeOfType<int>().Which.Should().BePositive();
    }

    [Fact]
    public async Task GenerationService_RecordsHeuristicOutcome_InTheDefaultMode()
    {
        var (input, matrix) = OptionalRouteOptimizationScenarios.CreateSyntheticCorpusScenario(20, 2, 54);
        var service = new ItineraryGenerationService(new StaticMatrixProvider(matrix));

        var activity = await RecordSolverOutcome(() => service.GenerateAsync(input, CancellationToken.None));

        activity.GetTagItem("solver.mode").Should().Be(nameof(SchedulingSolverMode.Heuristic));
        activity.GetTagItem("solver.outcome").Should().Be(SchedulingSolverOutcomes.Heuristic);
        activity.GetTagItem("csp.nodes_expanded").Should().BeNull();
    }

    [Fact]
    public async Task GenerationService_RecordsSearchLimitFallback_WhenCspStopsBeforeAnySolution()
    {
        var (input, matrix) = OptionalRouteOptimizationScenarios.CreateSyntheticCorpusScenario(20, 2, 54);
        var options = new SchedulingGenerationOptions
        {
            SolverMode = SchedulingSolverMode.Csp,
            Csp = new CspOptions { MaxNodes = 1, UseInitialIncumbent = false },
        };
        var service = new ItineraryGenerationService(new StaticMatrixProvider(matrix), options);

        Result<GeneratedItineraryPlan>? result = null;
        var activity = await RecordSolverOutcome(async () => result = await service.GenerateAsync(input, CancellationToken.None));

        result!.IsSuccess.Should().BeTrue();
        activity.GetTagItem("solver.outcome").Should().Be(SchedulingSolverOutcomes.HeuristicFallbackCspSearchLimit);
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
    /// Dừng theo số nút (không theo đồng hồ) nên cùng một yêu cầu luôn cho cùng một lịch trình.
    /// </summary>
    [Theory]
    [InlineData(20, 2, 61)]
    [InlineData(40, 4, 62)]
    public void Solve_IsDeterministic_ForTheSameInput(int candidateCount, int mandatoryCount, int seed)
    {
        var (input, matrix) = OptionalRouteOptimizationScenarios.CreateSyntheticCorpusScenario(candidateCount, mandatoryCount, seed);
        var (candidates, indices) = Index(input);

        var first = new CspItinerarySolver(new ItineraryScheduleEvaluator(Options), Options).Solve(input, matrix, candidates, indices);
        var second = new CspItinerarySolver(new ItineraryScheduleEvaluator(Options), Options).Solve(input, matrix, candidates, indices);

        first.Statistics.TimeLimitReached.Should().BeFalse();
        first.Schedule.Should().NotBeNull();
        second.Schedule!.VisitPoiIds.Should().Equal(first.Schedule!.VisitPoiIds);
        second.Statistics.NodesExpanded.Should().Be(first.Statistics.NodesExpanded);
    }

    /// <summary>
    /// Hồi quy: lời giải khởi đầu (chèn rẻ nhất) từng dùng mọi điểm tùy chọn và không giới hạn số điểm dừng,
    /// nên kết quả CSP có thể vượt MaxStops hoặc chứa điểm ngoài top-N, dù bản thân phép duyệt tôn trọng cả hai.
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
        bool useInitialIncumbent,
        bool polish)
    {
        var (input, matrix) = OptionalRouteOptimizationScenarios.CreateSyntheticCorpusScenario(candidateCount, mandatoryCount, seed);
        var (candidates, indices) = Index(input);
        var limits = new CspOptions
        {
            MaxStops = mandatoryCount + 2,
            MaxOptionalDomainSize = 3,
            UseInitialIncumbent = useInitialIncumbent,
            PolishWithLocalSearch = polish,
        };

        var result = new CspItinerarySolver(new ItineraryScheduleEvaluator(Options), Options, limits)
            .Solve(input, matrix, candidates, indices);

        result.Schedule.Should().NotBeNull();
        result.Schedule!.VisitPoiIds.Count.Should().BeLessThanOrEqualTo(limits.MaxStops);
        input.MandatoryPoiIds.Should().BeSubsetOf(result.Schedule.VisitPoiIds);
        result.Schedule.VisitPoiIds
            .Where(id => !input.MandatoryPoiIds.Contains(id))
            .Should().BeSubsetOf(TopRankedOptionalIds(input, matrix, limits.MaxOptionalDomainSize));
    }

    /// <summary>
    /// Lời giải khởi đầu chỉ là cận trên để cắt nhánh sớm: khi phép duyệt chạy hết cây, bật hay tắt nó
    /// phải cho cùng mục tiêu tối ưu trong cùng giới hạn MaxStops và miền giá trị.
    /// </summary>
    [Theory]
    [InlineData(20, 0, 71)]
    [InlineData(20, 1, 72)]
    [InlineData(40, 2, 73)]
    public void Solve_InitialIncumbentDoesNotChangeTheOptimum_UnderRestrictiveLimits(int candidateCount, int mandatoryCount, int seed)
    {
        var (input, matrix) = OptionalRouteOptimizationScenarios.CreateSyntheticCorpusScenario(candidateCount, mandatoryCount, seed);
        var (candidates, indices) = Index(input);
        CspOptions Limits(bool useInitialIncumbent) => new()
        {
            MaxStops = mandatoryCount + 2,
            MaxOptionalDomainSize = 3,
            MaxNodes = 10_000_000,
            TimeLimitMilliseconds = 60_000,
            UseInitialIncumbent = useInitialIncumbent,
        };

        var withIncumbent = new CspItinerarySolver(new ItineraryScheduleEvaluator(Options), Options, Limits(true))
            .Solve(input, matrix, candidates, indices);
        var withoutIncumbent = new CspItinerarySolver(new ItineraryScheduleEvaluator(Options), Options, Limits(false))
            .Solve(input, matrix, candidates, indices);

        withIncumbent.Statistics.SearchCompleted.Should().BeTrue();
        withoutIncumbent.Statistics.SearchCompleted.Should().BeTrue();
        withIncumbent.Statistics.BestObjective.Should().Be(withoutIncumbent.Statistics.BestObjective);
        withIncumbent.Statistics.InitialUpperBound.Should().BeGreaterThanOrEqualTo(withIncumbent.Statistics.BestObjective!.Value);
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

    private static long? ExhaustiveBest(RoutingContext ctx)
    {
        long? best = null;
        var sequence = new List<int>();

        void Visit()
        {
            var state = RouteState.Build(ctx, sequence);
            if (state is null)
            {
                return;
            }

            if (state.Count > 0 && state.HasAllMandatory && (best is null || state.Objective < best))
            {
                best = state.Objective;
            }

            for (var node = 0; node < ctx.Nodes.Count; node++)
            {
                if (!sequence.Contains(node))
                {
                    sequence.Add(node);
                    Visit();
                    sequence.RemoveAt(sequence.Count - 1);
                }
            }
        }

        Visit();
        return best;
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

    private static long[] TopRankedOptionalIds(GenerationInput input, RouteDurationMatrix matrix, int count)
    {
        var (candidates, indices) = Index(input);
        var ctx = RoutingContext.Create(input, matrix, candidates, indices, Options, Options.MiniRouting);
        return ctx.OptionalNodes.Take(count).Select(node => ctx.Nodes[node].Candidate.Id).ToArray();
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