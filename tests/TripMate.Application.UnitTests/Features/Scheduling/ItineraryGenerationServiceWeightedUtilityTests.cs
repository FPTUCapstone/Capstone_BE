using System.Diagnostics;
using FluentAssertions;
using TripMate.Application.Common.Interfaces;
using TripMate.Application.Features.Scheduling.Common;
using TripMate.Application.Features.Scheduling.Csp;
using TripMate.Application.Features.Scheduling.Csp.Objectives;
using TripMate.Application.Features.Scheduling.Personalization;
using TripMate.Application.Features.Scheduling.Routing;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;
using Xunit;

namespace TripMate.Application.UnitTests.Features.Scheduling;

public class ItineraryGenerationServiceWeightedUtilityTests
{
    private const long ExpectedJ = 9500L;
    private const long ExpectedPhysicalRestJ = 9000L;
    private const int ExpectedStandardTravelMinutes = 30;
    private const int ExpectedPhysicalRestTravelMinutes = 40;

    private static GenerationCandidate CreateCandidate(
        long id,
        string name,
        string category,
        decimal baseScore,
        int visitDurationMinutes = 60,
        bool hasShelter = true)
    {
        return new GenerationCandidate(
            Id: id,
            Name: name,
            Location: new RoutePoint(16.05m + id * 0.01m, 108.20m + id * 0.01m),
            VisitDurationMinutes: visitDurationMinutes,
            EstimatedVisitCost: 50_000m,
            OpeningHours: [new GenerationOpeningHours(2, new TimeOnly(0, 0), new TimeOnly(23, 59))],
            CategoryName: category,
            HasShelter: hasShelter,
            TripMateBaseScore: baseScore,
            EffectiveDesirabilityScore: baseScore);
    }

    private static (GenerationInput Input, WeightedRequestInputs WeightedInputs, FixedRouteDurationProvider RouteProvider) CreateFixture(
        RestPreference restPreference = RestPreference.None,
        bool includeRestPoi = false)
    {
        var candidatesList = new List<GenerationCandidate>
        {
            CreateCandidate(1, "Museum", "Culture", 0.80m, visitDurationMinutes: includeRestPoi ? 120 : 60),
            CreateCandidate(2, "Nature Park", "Nature", 0.60m, visitDurationMinutes: 60)
        };

        if (includeRestPoi)
        {
            candidatesList.Add(CreateCandidate(3, "Highlands Coffee", "Food & Drink", 0.70m, visitDurationMinutes: 30, hasShelter: true));
        }

        var candidates = candidatesList.ToArray();

        var input = new GenerationInput(
            StartAtUtc: new DateTimeOffset(2026, 10, 20, 8, 0, 0, TimeSpan.Zero),
            TimeZone: TimeZoneInfo.Utc,
            Start: new RoutePoint(16.05m, 108.20m),
            End: new RoutePoint(16.05m, 108.20m),
            AvailableMinutes: 480,
            TransportMode: TransportMode.Motorbike,
            RestPreference: restPreference,
            BudgetVnd: 500_000m,
            Candidates: candidates,
            MandatoryPoiIds: [1, 2]);

        var customOptions = new PersonalizationOptions
        {
            Mu = 0.002,
            RhoStops = 0.30,
            GammaDefault = 0.70,
            GammaLoved = 0.85,
            Pace = new Dictionary<TravelerPace, PaceParams>
            {
                [TravelerPace.Moderate] = new PaceParams(
                    LambdaPerMin: 0.005,
                    MaxStopsPerDay: 1,
                    MaxDailyEffort: 480,
                    RestAfterMin: 240)
            }
        };

        var paramsFactory = new PersonalParamsFactory(Microsoft.Extensions.Options.Options.Create(customOptions));
        var inputsFactory = new WeightedRequestInputsFactory(paramsFactory, new ConstantTimeFitFactory());
        var weightedInputs = inputsFactory.Create(candidates, null, input, PoiRankingOutcomeCategory.Success);

        var routeProvider = new FixedRouteDurationProvider(10);
        return (input, weightedInputs, routeProvider);
    }

    /// <summary>
    /// Captures the solver outcome activity using an ActivityListener correlated with the parent activity's TraceId,
    /// ensuring complete isolation when xUnit tests run concurrently.
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

        recorded.Should().NotBeNull("An outcome activity must be recorded by the scheduling service");
        return recorded!;
    }

    private static async Task<(RoutingContext Ctx, WeightedObjectiveInputs ObjInputs)> CreateRoutingContextAndInputs(
        GenerationInput input,
        WeightedRequestInputs weightedInputs,
        SchedulingGenerationOptions options,
        FixedRouteDurationProvider routeProvider)
    {
        var matrixPoints = new List<RoutePoint> { input.Start };
        matrixPoints.AddRange(input.Candidates.Select(c => c.Location));
        matrixPoints.Add(input.End);
        var matrix = await routeProvider.GetMatrixAsync(matrixPoints, input.TransportMode, CancellationToken.None);

        var matrixCandidates = input.Candidates.ToArray();
        var candidateIndices = matrixCandidates.Select((c, idx) => (c.Id, idx + 1)).ToDictionary(x => x.Id, x => x.Item2);
        var ctx = RoutingContext.Create(input, matrix, matrixCandidates, candidateIndices, options, options.MiniRouting);
        var objInputs = ObjectiveKernelFactory.BuildWeightedInputs(ctx, weightedInputs);
        return (ctx, objInputs);
    }

    private static void AssertPlanMatchesInternalSchedule(
        GeneratedItineraryPlan plan,
        EvaluatedItinerarySchedule internalSchedule,
        int expectedTotalTravelMinutes)
    {
        plan.TotalDurationMinutes.Should().Be(internalSchedule.TotalDurationMinutes);
        plan.EndAtUtc.Should().Be(internalSchedule.EndAtUtc);
        plan.TotalEstimatedCost.Should().Be(internalSchedule.TotalEstimatedCost);
        plan.Items.Count.Should().Be(internalSchedule.Plan.Items.Count);

        var planVisitIds = plan.Items
            .Where(i => i.Kind == ItineraryItemKind.Visit && i.PointOfInterestId.HasValue)
            .Select(i => i.PointOfInterestId!.Value)
            .ToArray();
        planVisitIds.Should().Equal(internalSchedule.VisitPoiIds);

        internalSchedule.TotalMatrixTravelMinutes.Should().Be(expectedTotalTravelMinutes);
        internalSchedule.TotalDurationMinutes.Should().Be(internalSchedule.Plan.TotalDurationMinutes);
        internalSchedule.EndAtUtc.Should().Be(internalSchedule.Plan.EndAtUtc);
        internalSchedule.TotalEstimatedCost.Should().Be(internalSchedule.Plan.TotalEstimatedCost);

        var planItems = plan.Items.OrderBy(i => i.SequenceNo).ToArray();
        var internalItems = internalSchedule.Plan.Items.OrderBy(i => i.SequenceNo).ToArray();

        for (var i = 0; i < planItems.Length; i++)
        {
            planItems[i].SequenceNo.Should().Be(internalItems[i].SequenceNo);
            planItems[i].Kind.Should().Be(internalItems[i].Kind);
            planItems[i].PointOfInterestId.Should().Be(internalItems[i].PointOfInterestId);
            planItems[i].PointOfInterestName.Should().Be(internalItems[i].PointOfInterestName);
            planItems[i].PlannedArrivalUtc.Should().Be(internalItems[i].PlannedArrivalUtc);
            planItems[i].PlannedDepartureUtc.Should().Be(internalItems[i].PlannedDepartureUtc);
            planItems[i].IsMandatory.Should().Be(internalItems[i].IsMandatory);
            planItems[i].EstimatedCost.Should().Be(internalItems[i].EstimatedCost);
            planItems[i].RecommendationReason.Should().Be(internalItems[i].RecommendationReason);
            planItems[i].TravelDurationToNextMinutes.Should().Be(internalItems[i].TravelDurationToNextMinutes);
            planItems[i].TravelDistanceToNextMeters.Should().Be(internalItems[i].TravelDistanceToNextMeters);
            planItems[i].TransportModeToNext.Should().Be(internalItems[i].TransportModeToNext);
        }
    }

    [Fact]
    public async Task GenerationService_WeightedUtility_SetsPlanModelObjective_MatchingAuthoritativeEvaluateAndFixedExpectedValue()
    {
        var (input, weightedInputs, routeProvider) = CreateFixture();
        var options = new SchedulingGenerationOptions
        {
            EnableOptionalRouteOptimization = true,
            SolverMode = SchedulingSolverMode.Csp,
            Csp = new CspOptions
            {
                Mode = ObjectiveMode.WeightedUtility,
                MaxNodes = 50_000,
                TimeLimitMilliseconds = 5000,
                PolishWithLocalSearch = false
            }
        };

        OptimizationResult? capturedResult = null;
        var service = new ItineraryGenerationService(routeProvider, options)
        {
            OnOptimizationCompleted = optResult => capturedResult = optResult
        };

        GeneratedItineraryPlan? plan = null;
        var activity = await RecordSolverOutcome(async () =>
        {
            var result = await service.GenerateAsync(input, weightedInputs, CancellationToken.None);
            result.IsSuccess.Should().BeTrue();
            plan = result.Value;
        });

        // 1. Assert recorded outcome is strictly Csp (safely correlated via TraceId)
        activity.GetTagItem("solver.outcome").Should().Be(SchedulingSolverOutcomes.Csp);
        capturedResult.Should().NotBeNull("OptimizationResult must be captured from the exact internal optimizer run");
        plan.Should().NotBeNull();

        // 2. Assert returned plan matches the exact internal schedule that was scored
        AssertPlanMatchesInternalSchedule(plan!, capturedResult!.Schedule, ExpectedStandardTravelMinutes);

        // 3. Compute authoritative objective directly on the exact internal schedule
        var (ctx, objInputs) = await CreateRoutingContextAndInputs(input, weightedInputs, options, routeProvider);
        var authoritativeJ = ObjectiveCalculator.Evaluate(objInputs, ctx, capturedResult.Schedule);

        // 4. Assert ModelObjective matches locked deterministic expectation (9500L) and authoritative replay
        authoritativeJ.Should().Be(ExpectedJ);
        plan!.ModelObjective.Should().NotBeNull();
        plan.ModelObjective.Value.Should().Be(ExpectedJ);
        plan.ModelObjective.Value.Should().Be(authoritativeJ);
    }

    [Fact]
    public async Task GenerationService_WeightedUtility_HeuristicFallback_SetsAuthoritativeModelObjective()
    {
        var (input, weightedInputs, routeProvider) = CreateFixture();

        // Force CSP fallback to HeuristicFallbackCspSearchLimit (MaxNodes = 1, MaxStops = 1 while mandatory = 2)
        var options = new SchedulingGenerationOptions
        {
            EnableOptionalRouteOptimization = true,
            SolverMode = SchedulingSolverMode.Csp,
            Csp = new CspOptions
            {
                Mode = ObjectiveMode.WeightedUtility,
                MaxNodes = 1,
                MaxStops = 1
            }
        };

        OptimizationResult? capturedResult = null;
        var service = new ItineraryGenerationService(routeProvider, options)
        {
            OnOptimizationCompleted = optResult => capturedResult = optResult
        };

        GeneratedItineraryPlan? plan = null;
        var activity = await RecordSolverOutcome(async () =>
        {
            var result = await service.GenerateAsync(input, weightedInputs, CancellationToken.None);
            result.IsSuccess.Should().BeTrue();
            plan = result.Value;
        });

        // 1. Assert outcome is strictly HeuristicFallbackCspSearchLimit (safely correlated via TraceId)
        activity.GetTagItem("solver.outcome")
            .Should().Be(SchedulingSolverOutcomes.HeuristicFallbackCspSearchLimit);

        capturedResult.Should().NotBeNull("OptimizationResult must be captured from the exact internal optimizer run");
        plan.Should().NotBeNull();

        // 2. Assert returned plan matches the exact internal schedule that was scored
        AssertPlanMatchesInternalSchedule(plan!, capturedResult!.Schedule, ExpectedStandardTravelMinutes);

        // 3. Compute authoritative objective directly on the exact internal schedule
        var (ctx, objInputs) = await CreateRoutingContextAndInputs(input, weightedInputs, options, routeProvider);
        var authoritativeJ = ObjectiveCalculator.Evaluate(objInputs, ctx, capturedResult.Schedule);

        // 4. Assert ModelObjective matches locked deterministic expectation (9500L) and authoritative replay
        plan!.ModelObjective.Should().NotBeNull("Fallback plan in WeightedUtility mode must have ModelObjective");
        plan.ModelObjective.Value.Should().Be(ExpectedJ);
        plan.ModelObjective.Value.Should().Be(authoritativeJ);
    }

    [Fact]
    public async Task GenerationService_WeightedUtility_HeuristicFallback_WithPhysicalRestPoi_SetsAuthoritativeModelObjective()
    {
        var (input, weightedInputs, routeProvider) = CreateFixture(
            restPreference: RestPreference.Frequent,
            includeRestPoi: true);

        // Force CSP fallback to HeuristicFallbackCspSearchLimit (MaxNodes = 1, MaxStops = 1 while mandatory = 2)
        var options = new SchedulingGenerationOptions
        {
            EnableOptionalRouteOptimization = true,
            SolverMode = SchedulingSolverMode.Csp,
            Csp = new CspOptions
            {
                Mode = ObjectiveMode.WeightedUtility,
                MaxNodes = 1,
                MaxStops = 1
            }
        };

        OptimizationResult? capturedResult = null;
        var service = new ItineraryGenerationService(routeProvider, options)
        {
            OnOptimizationCompleted = optResult => capturedResult = optResult
        };

        GeneratedItineraryPlan? plan = null;
        var activity = await RecordSolverOutcome(async () =>
        {
            var result = await service.GenerateAsync(input, weightedInputs, CancellationToken.None);
            result.IsSuccess.Should().BeTrue();
            plan = result.Value;
        });

        // 1. Assert recorded outcome is strictly HeuristicFallbackCspSearchLimit (safely correlated via TraceId)
        activity.GetTagItem("solver.outcome")
            .Should().Be(SchedulingSolverOutcomes.HeuristicFallbackCspSearchLimit);

        capturedResult.Should().NotBeNull("OptimizationResult must be captured from the exact internal optimizer run");
        plan.Should().NotBeNull();

        // 2. Assert physical rest POI was inserted by evaluator into the plan
        var restItem = plan!.Items.FirstOrDefault(item => item.Kind == ItineraryItemKind.Rest);
        restItem.Should().NotBeNull("Frequent rest preference with a qualified rest candidate must produce a physical rest item");
        restItem!.PointOfInterestId.Should().Be(3L, "Highlands Coffee (ID 3) should be selected as the rest stop");

        // 3. Assert returned plan matches the exact internal schedule that was scored
        AssertPlanMatchesInternalSchedule(plan, capturedResult!.Schedule, ExpectedPhysicalRestTravelMinutes);

        // 4. Compute authoritative objective directly on the exact internal schedule
        var (ctx, objInputs) = await CreateRoutingContextAndInputs(input, weightedInputs, options, routeProvider);
        var authoritativeJ = ObjectiveCalculator.Evaluate(objInputs, ctx, capturedResult.Schedule);

        // 5. Assert ModelObjective matches locked deterministic expectation (9000L) and authoritative replay
        plan.ModelObjective.Should().NotBeNull("Fallback plan in WeightedUtility mode with physical rest POI must compute ModelObjective");
        plan.ModelObjective.Value.Should().Be(ExpectedPhysicalRestJ);
        plan.ModelObjective.Value.Should().Be(authoritativeJ);
    }

    private sealed class FixedRouteDurationProvider(int minutes) : IRouteDurationProvider
    {
        public Task<RouteDurationMatrix> GetMatrixAsync(
            IReadOnlyList<RoutePoint> points,
            TransportMode transportMode,
            CancellationToken cancellationToken)
        {
            var table = new int[points.Count, points.Count];
            for (var i = 0; i < points.Count; i++)
            {
                for (var j = 0; j < points.Count; j++)
                {
                    table[i, j] = i == j ? 0 : minutes;
                }
            }

            return Task.FromResult(RouteDurationMatrix.Create(table));
        }
    }
}