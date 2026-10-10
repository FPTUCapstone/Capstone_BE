using FluentAssertions;

using TripMate.Application.Features.Scheduling.Common;
using TripMate.Application.Features.Scheduling.Routing;
using TripMate.Application.UnitTests.Features.Scheduling.Fixtures;
using TripMate.Domain.Enums;

namespace TripMate.Application.UnitTests.Features.Scheduling.Routing;

public class MiniRoutingSolverTests
{
    private static readonly SchedulingGenerationOptions Options = new();

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public void CanInsertAndCanRemove_AgreeWithFullSimulation(int seed)
    {
        var (input, matrix) = TightScenario(seed);
        var (ctx, _, _) = Context(input, matrix);
        var state = MiniRoutingSolver.Construct(ctx, CancellationToken.None);
        state.Should().NotBeNull();

        for (var node = 0; node < ctx.Nodes.Count; node++)
        {
            if (state!.Contains(node))
            {
                continue;
            }

            for (var position = 0; position <= state.Count; position++)
            {
                var fast = state.CanInsert(position, node, out var delta);
                var full = RouteState.Build(ctx, state.WithInserted(position, node));

                fast.Should().Be(full is not null, $"insert node {node} at {position}");
                if (fast)
                {
                    delta.Should().Be(full!.TravelMinutes - state.TravelMinutes);
                }
            }
        }

        for (var position = 0; position < state!.Count; position++)
        {
            var fast = state.CanRemove(position, out var delta);
            var removable = !ctx.Nodes[state.Sequence[position]].IsMandatory && state.Count > 1;
            var full = removable ? RouteState.Build(ctx, state.WithRemoved(position)) : null;

            fast.Should().Be(full is not null, $"remove position {position}");
            if (fast)
            {
                delta.Should().Be(full!.TravelMinutes - state.TravelMinutes);
            }
        }
    }

    [Fact]
    public void Build_ComputesLatestStartsThatKeepTheRestOfTheRouteFeasible()
    {
        var (input, matrix) = TightScenario(7);
        var (ctx, _, _) = Context(input, matrix);
        var state = MiniRoutingSolver.Construct(ctx, CancellationToken.None)!;

        for (var i = 0; i < state.Count; i++)
        {
            state.Earliest[i].Should().BeLessThanOrEqualTo(state.Latest[i]);
        }
    }

    [Theory]
    [InlineData(20, 2, 11)]
    [InlineData(40, 4, 12)]
    [InlineData(30, 3, 13)]
    public void Solve_ReturnsValidScheduleWithAllMandatoryPois(int candidateCount, int mandatoryCount, int seed)
    {
        var (input, matrix) = OptionalRouteOptimizationScenarios.CreateSyntheticCorpusScenario(candidateCount, mandatoryCount, seed);
        var (_, candidates, indices) = Context(input, matrix);
        var evaluator = new ItineraryScheduleEvaluator(Options);

        var result = new MiniRoutingSolver(evaluator, Options).Solve(input, matrix, candidates, indices);

        result.Should().NotBeNull();
        result!.Schedule.VisitPoiIds.Should().Contain(input.MandatoryPoiIds);
        result.Schedule.TotalDurationMinutes.Should().BeLessThanOrEqualTo(input.AvailableMinutes);
        result.Schedule.TotalEstimatedCost.Should().BeLessThanOrEqualTo(input.BudgetVnd!.Value);
        var validate = () => new GeneratedItineraryInvariantValidator(Options).Validate(input, matrix, candidates, result.Schedule.Plan);
        validate.Should().NotThrow();
    }

    [Fact]
    public void Solve_NeverTravelsMoreThanItsOwnStartingRoute()
    {
        var (input, matrix) = OptionalRouteOptimizationScenarios.CreateZigzagScenario();
        var (_, candidates, indices) = Context(input, matrix);

        var result = new MiniRoutingSolver(new ItineraryScheduleEvaluator(Options), Options).Solve(input, matrix, candidates, indices);

        result.Should().NotBeNull();
        result!.Statistics.BestObjective.Should().BeLessThanOrEqualTo(result.Statistics.InitialObjective);
    }

    [Fact]
    public void Solve_ReturnsNull_WhenAMandatoryPoiIsClosedAllDay()
    {
        var (input, matrix) = OptionalRouteOptimizationScenarios.CreateSyntheticCorpusScenario(10, 2, 21);
        var closedId = input.MandatoryPoiIds.First();
        input = input with
        {
            Candidates = input.Candidates
                .Select(c => c.Id == closedId ? c with { OpeningHours = [] } : c)
                .ToArray(),
        };
        var (_, candidates, indices) = Context(input, matrix);

        var result = new MiniRoutingSolver(new ItineraryScheduleEvaluator(Options), Options).Solve(input, matrix, candidates, indices);

        result.Should().BeNull();
    }

    [Fact]
    public async Task GenerationService_UsesMiniRouting_WhenSolverModeIsMiniRouting()
    {
        var (input, matrix) = OptionalRouteOptimizationScenarios.CreateSyntheticCorpusScenario(20, 2, 31);
        var options = new SchedulingGenerationOptions { SolverMode = SchedulingSolverMode.MiniRouting };
        var service = new ItineraryGenerationService(new StaticMatrixProvider(matrix), options);

        var result = await service.GenerateAsync(input, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Items
            .Where(item => item.Kind == ItineraryItemKind.Visit && item.IsMandatory)
            .Select(item => item.PointOfInterestId!.Value)
            .Should().BeEquivalentTo(input.MandatoryPoiIds);
    }

    [Theory]
    [InlineData(new[] { 1, 2, 3, 4, 5 }, 1, 2, 3, false, new[] { 1, 4, 5, 2, 3 })]
    [InlineData(new[] { 1, 2, 3, 4, 5 }, 3, 2, 0, true, new[] { 5, 4, 1, 2, 3 })]
    [InlineData(new[] { 1, 2, 3, 4, 5 }, 0, 1, 4, false, new[] { 2, 3, 4, 5, 1 })]
    public void MoveSegment_PlacesTheSegmentAtTheTargetPosition(
        int[] sequence,
        int from,
        int length,
        int to,
        bool reversed,
        int[] expected)
    {
        LocalSearchOperators.MoveSegment(sequence, from, length, to, reversed).Should().Equal(expected);
    }

    private static (RoutingContext Ctx, GenerationCandidate[] Candidates, Dictionary<long, int> Indices) Context(
        GenerationInput input,
        RouteDurationMatrix matrix)
    {
        var candidates = input.Candidates.ToArray();
        var indices = candidates.Select((c, i) => (c.Id, Index: i + 1)).ToDictionary(x => x.Id, x => x.Index);
        var ctx = RoutingContext.Create(input, matrix, candidates, indices, Options, new MiniRoutingOptions());
        return (ctx, candidates, indices);
    }

    private static (GenerationInput Input, RouteDurationMatrix Matrix) TightScenario(int seed)
    {
        var (input, matrix) = OptionalRouteOptimizationScenarios.CreateSyntheticCorpusScenario(25, 2, seed);
        var random = new Random(seed * 97);
        var mandatory = input.MandatoryPoiIds.ToHashSet();
        var candidates = input.Candidates
            .Select(c => mandatory.Contains(c.Id)
                ? c
                : c with
                {
                    OpeningHours =
                    [
                        new GenerationOpeningHours(2, new TimeOnly(random.Next(8, 14), 0), new TimeOnly(random.Next(14, 19), 0)),
                    ],
                })
            .ToArray();
        return (input with { Candidates = candidates, BudgetVnd = 120_000m }, matrix);
    }

    private sealed class StaticMatrixProvider(RouteDurationMatrix matrix) : IRouteDurationProvider
    {
        public Task<RouteDurationMatrix> GetMatrixAsync(
            IReadOnlyList<RoutePoint> points,
            TransportMode transportMode,
            CancellationToken cancellationToken) => Task.FromResult(matrix);
    }
}