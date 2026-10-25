using FluentAssertions;

using TripMate.Application.Features.Scheduling.Common;
using TripMate.Application.Features.Scheduling.Csp;
using TripMate.Application.Features.Scheduling.Csp.Objectives;
using TripMate.Application.Features.Scheduling.Personalization;
using TripMate.Application.Features.Scheduling.Routing;
using TripMate.Application.UnitTests.Features.Scheduling.Fixtures;
using TripMate.Domain.Constants;
using TripMate.Domain.Enums;

using Xunit;

namespace TripMate.Application.UnitTests.Features.Scheduling.Objectives;

public class ReturnTravelObjectiveTests
{
    private const double LambdaPerMin = 0.25;
    private static readonly SchedulingGenerationOptions Options = new()
    {
        TransitionBufferMinutes = 10,
        FinalReturnBufferMinutes = 15,
    };

    [Fact]
    public void OnePoi_ChargesIncomingAndReturnRawTravelExactlyOnce()
    {
        var fixture = CreateFixture(
            new int[,]
            {
                { 0, 7, 99 },
                { 5, 0, 11 },
                { 3, 4, 0 },
            },
            mandatoryPoiIds: [101]);
        var route = RouteState.Build(fixture.Context, [0])!;
        var schedule = Evaluate(fixture, [fixture.Candidates[0]]);
        var soft = default(SoftState);

        var visit = ObjectiveCalculator.VisitTerm(
            fixture.Inputs,
            node: 0,
            start: route.Earliest[0],
            end: route.Earliest[0] + fixture.Context.Nodes[0].VisitMinutes,
            travelRaw: 7,
            wait: 0,
            groupCountBefore: 0,
            ref soft);
        var returning = ObjectiveCalculator.ReturnTerm(fixture.Inputs, returnRaw: 11);
        var replay = ObjectiveCalculator.Replay(
            fixture.Inputs,
            fixture.Context,
            route.Sequence,
            route.Earliest,
            new int[fixture.Inputs.DiversityGroupCount]);
        var evaluated = ObjectiveCalculator.Evaluate(fixture.Inputs, fixture.Context, schedule);

        fixture.Context.StartMatrixIndex.Should().Be(0);
        fixture.Context.EndMatrixIndex.Should().Be(2);
        schedule.TotalMatrixTravelMinutes.Should().Be(7 + 11);
        visit.Should().Be(-7_500, "gain 1.0 minus lambda 0.25 x 7 incoming minutes");
        returning.Should().Be(-27_500, "lambda 0.25 x 11 return minutes");
        replay.Should().Be(visit + returning);
        evaluated.Should().Be(visit + returning);
    }

    [Fact]
    public void RoutesDifferingOnlyByReturnTravel_DifferByExactScaledReturnCost()
    {
        var shortReturn = CreateFixture(
            new int[,]
            {
                { 0, 7, 99 },
                { 5, 0, 9 },
                { 3, 4, 0 },
            },
            mandatoryPoiIds: [101],
            lambdaPerMin: 0.00005);
        var longReturn = CreateFixture(
            new int[,]
            {
                { 0, 7, 99 },
                { 5, 0, 11 },
                { 3, 4, 0 },
            },
            mandatoryPoiIds: [101],
            lambdaPerMin: 0.00005);

        var shortRoute = RouteState.Build(shortReturn.Context, [0])!;
        var longRoute = RouteState.Build(longReturn.Context, [0])!;
        var shortJ = Replay(shortReturn, shortRoute);
        var longJ = Replay(longReturn, longRoute);

        shortJ.Should().Be(9_992, "ToEven rounds the scaled 9-minute return cost from 4.5 to 4");
        longJ.Should().Be(9_990, "ToEven rounds the scaled 11-minute return cost from 5.5 to 6");
        (shortJ - longJ).Should().Be(2, "only the rounded raw return costs differ");
    }

    [Fact]
    public void AsymmetricTwoPoiRoute_IncrementalReplayEvaluateComparerAndPublishedSolverObjectiveMatch()
    {
        var fixture = CreateFixture(
            new int[,]
            {
                { 0, 7, 29, 80 },
                { 31, 0, 13, 41 },
                { 37, 23, 0, 17 },
                { 43, 47, 53, 0 },
            },
            mandatoryPoiIds: [101, 102]);
        var cspOptions = new CspOptions
        {
            Mode = ObjectiveMode.WeightedUtility,
            MaxStops = 2,
            TimeLimitMilliseconds = 10_000,
        };
        var solver = new CspItinerarySolver(new ItineraryScheduleEvaluator(Options), Options, cspOptions);
        var result = solver.Solve(
            fixture.Input,
            fixture.Matrix,
            fixture.Candidates,
            fixture.Indices,
            initialIncumbent: null,
            fixture.RequestInputs,
            CancellationToken.None);
        result.Schedule.Should().NotBeNull();
        var schedule = result.Schedule!;
        var sequence = schedule.VisitPoiIds.Select(id => fixture.NodeOf[id]).ToArray();
        var route = RouteState.Build(fixture.Context, sequence);
        route.Should().NotBeNull();

        var buffers = new SearchBuffers(fixture.Context.Nodes.Count, fixture.Context.Nodes.Count);
        var kernel = new WeightedUtilityKernel(fixture.Inputs, cspOptions);
        kernel.Initialize(fixture.Context, buffers, CreatePrecomputed(fixture.Context), new CspStatistics());
        for (var depth = 0; depth < sequence.Length; depth++)
        {
            var node = sequence[depth];
            buffers.Sequence[depth] = node;
            buffers.Departure[depth] = route.Earliest[depth] + fixture.Context.Nodes[node].VisitMinutes;
            buffers.Contains[node] = true;
            kernel.OnPush(
                depth,
                node,
                route.Earliest[depth],
                depth == 0
                    ? fixture.Context.StartMatrixIndex
                    : fixture.Context.Nodes[sequence[depth - 1]].MatrixIndex);
        }

        var lastMatrix = fixture.Context.Nodes[sequence[^1]].MatrixIndex;
        kernel.AcceptIncumbent(sequence.Length, buffers.Departure[sequence.Length - 1], lastMatrix, route.TravelMinutes);
        kernel.IncumbentObjectiveForTest.Should().NotBeNull();
        var incremental = kernel.IncumbentObjectiveForTest!.Value;
        var replay = Replay(fixture, route!);
        var evaluated = ObjectiveCalculator.Evaluate(fixture.Inputs, fixture.Context, schedule);
        var comparer = new WeightedScheduleComparer(
            fixture.Inputs,
            fixture.Context,
            fixture.NodeOf,
            fixture.Indices,
            new int[fixture.Inputs.DiversityGroupCount],
            new List<GeneratedItineraryItem>());

        fixture.Context.RawTravel(lastMatrix, fixture.Context.EndMatrixIndex).Should().BeOneOf(17, 41);
        incremental.Should().Be(replay);
        evaluated.Should().Be(replay);
        comparer.GetObjective(schedule).Should().Be(replay);
        schedule.Plan.ModelObjective.Should().Be(replay);
    }

    [Fact]
    public void PhysicalRestAfterLastVisit_ReturnsFromRestLocationAndChargesEachRawLegOnce()
    {
        var visit = CreateCandidate(101, "Mandatory", visitDurationMinutes: 120);
        var rest = CreateCandidate(201, "Cafe", visitDurationMinutes: 30) with
        {
            CategoryName = PoiCategoryNames.FoodAndDrink,
            HasShelter = true,
        };
        var fixture = CreateFixture(
            new int[,]
            {
                { 0, 7, 31, 91 },
                { 5, 0, 5, 40 },
                { 6, 8, 0, 13 },
                { 9, 10, 11, 0 },
            },
            mandatoryPoiIds: [101],
            restPreference: RestPreference.Frequent,
            candidates: [visit, rest]);

        var schedule = Evaluate(fixture, [visit]);
        var evaluated = ObjectiveCalculator.Evaluate(fixture.Inputs, fixture.Context, schedule);

        schedule.Plan.Items.Should().ContainSingle(item =>
            item.Kind == ItineraryItemKind.Rest && item.PointOfInterestId == rest.Id);
        schedule.TotalMatrixTravelMinutes.Should().Be(7 + 5 + 13);
        evaluated.Should().Be(-52_500,
            "the objective charges start-to-visit, visit-to-rest, and rest-to-end raw legs exactly once");
    }

    private static long Replay(Fixture fixture, RouteState route) =>
        ObjectiveCalculator.Replay(
            fixture.Inputs,
            fixture.Context,
            route.Sequence,
            route.Earliest,
            new int[fixture.Inputs.DiversityGroupCount]);

    private static EvaluatedItinerarySchedule Evaluate(
        Fixture fixture,
        IReadOnlyList<GenerationCandidate> sequence) =>
        new ItineraryScheduleEvaluator(Options).Evaluate(
            fixture.Input,
            sequence,
            fixture.Matrix,
            fixture.Candidates,
            fixture.Indices,
            CancellationToken.None)
        ?? throw new InvalidOperationException("The controlled route must be evaluator-feasible.");

    private static Fixture CreateFixture(
        int[,] minutes,
        IReadOnlyCollection<long> mandatoryPoiIds,
        RestPreference restPreference = RestPreference.None,
        IReadOnlyList<GenerationCandidate>? candidates = null,
        double lambdaPerMin = LambdaPerMin)
    {
        GenerationCandidate[] defaultCandidates = [CreateCandidate(101, "POI 1"), CreateCandidate(102, "POI 2")];
        candidates ??= defaultCandidates[..(minutes.GetLength(0) - 2)];
        var input = OptionalRouteOptimizationScenarios.CreateInput(
            availableMinutes: 600,
            candidates,
            mandatoryPoiIds,
            restPreference,
            budgetVnd: null);
        var matrix = RouteDurationMatrix.Create(minutes);
        var indices = candidates.Select((candidate, index) => (candidate.Id, Index: index + 1))
            .ToDictionary(x => x.Id, x => x.Index);
        var context = CspItinerarySolver.CreatePrimaryContext(input, matrix, candidates, indices, Options);
        var requestInputs = new WeightedRequestInputs(
            context.Nodes.ToDictionary(node => node.Candidate.Id, _ => 1.0),
            CreatePersonalParams(lambdaPerMin),
            new ConstantTimeFitFactory(),
            "return-term-test");
        var inputs = ObjectiveKernelFactory.BuildWeightedInputs(context, requestInputs);
        var nodeOf = context.Nodes.Select((node, index) => (node.Candidate.Id, Index: index))
            .ToDictionary(x => x.Id, x => x.Index);

        return new Fixture(input, matrix, candidates, indices, context, inputs, requestInputs, nodeOf);
    }

    private static GenerationCandidate CreateCandidate(long id, string name, int visitDurationMinutes = 30) =>
        OptionalRouteOptimizationScenarios.CreateCandidate(
            id,
            name,
            visitDurationMinutes,
            cost: 0m,
            effectiveDesirabilityScore: 1m) with
        {
            CategoryName = PoiCategoryNames.General,
            HasShelter = true,
        };

    private static PersonalParams CreatePersonalParams(double lambdaPerMin) => new(
        lambdaPerMin,
        Mu: 0,
        MaxStopsPerDay: int.MaxValue,
        MaxDailyEffort: int.MaxValue,
        RestAfterMin: int.MaxValue,
        MaxConsecutiveOutdoor: int.MaxValue,
        SunriseFactor: 1,
        GammaDefault: 1,
        GammaLoved: 1,
        LovedCategories: new HashSet<string>(),
        RhoOutdoor: 0,
        RhoRest: 0,
        RhoStops: 0,
        RhoEffortPerHour: 0,
        ConfigVersion: "return-term-test");

    private static SearchPrecomputed CreatePrecomputed(RoutingContext context)
    {
        var nodeCount = context.Nodes.Count;
        var initialDomain = Enumerable.Range(0, nodeCount).ToArray();
        return new SearchPrecomputed(
            new int[nodeCount],
            new int[nodeCount],
            new int[nodeCount],
            new int[nodeCount],
            new int[nodeCount],
            minReturnTravel: 0,
            mandatoryWeight: 100,
            initialDomain,
            Enumerable.Repeat(true, nodeCount).ToArray());
    }

    private sealed record Fixture(
        GenerationInput Input,
        RouteDurationMatrix Matrix,
        IReadOnlyList<GenerationCandidate> Candidates,
        IReadOnlyDictionary<long, int> Indices,
        RoutingContext Context,
        WeightedObjectiveInputs Inputs,
        WeightedRequestInputs RequestInputs,
        IReadOnlyDictionary<long, int> NodeOf);
}