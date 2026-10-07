using FluentAssertions;

using TripMate.Application.Features.Scheduling.Common;
using TripMate.Application.UnitTests.Features.Scheduling.Fixtures;
using TripMate.Domain.Enums;

using Xunit;

namespace TripMate.Application.UnitTests.Features.Scheduling;

public sealed class GeneratedItineraryInvariantValidatorTests
{
    private readonly GeneratedItineraryInvariantValidator _validator = new();

    [Fact]
    public void Validate_AcceptsValidOptimizedPlan()
    {
        var (input, matrix) = OptionalRouteOptimizationScenarios.CreateZigzagScenario();
        var evaluator = new ItineraryScheduleEvaluator();
        var optimizer = new OptionalRouteOptimizer(evaluator);

        var mandatoryCandidates = input.MandatoryPoiIds
            .Select(id => input.Candidates.First(c => c.Id == id))
            .ToArray();
        var matrixCandidates = input.Candidates.ToArray();
        var candidateMatrixIndices = new Dictionary<long, int>();
        for (var i = 0; i < matrixCandidates.Length; i++)
        {
            candidateMatrixIndices[matrixCandidates[i].Id] = i + 1;
        }

        var optResult = optimizer.Optimize(
            input,
            mandatoryCandidates,
            matrix,
            matrixCandidates,
            candidateMatrixIndices,
            CancellationToken.None);

        optResult.Should().NotBeNull();
        var act = () => _validator.Validate(input, matrix, matrixCandidates, optResult!.Schedule.Plan);
        act.Should().NotThrow();
    }

    [Fact]
    public void Validate_ThrowsWhenPlanHasNoItems()
    {
        var (input, matrix) = OptionalRouteOptimizationScenarios.CreateZigzagScenario();
        var emptyPlan = new GeneratedItineraryPlan([], input.StartAtUtc, 0, 0m);

        var act = () => _validator.Validate(input, matrix, input.Candidates.ToArray(), emptyPlan);
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*no items*");
    }

    [Fact]
    public void Validate_ThrowsWhenSequenceNumbersNotContiguous()
    {
        var (input, matrix) = OptionalRouteOptimizationScenarios.CreateZigzagScenario();
        var evaluator = new ItineraryScheduleEvaluator();
        var matrixCandidates = input.Candidates.ToArray();
        var candidateMatrixIndices = matrixCandidates
            .Select((c, i) => (c.Id, Index: i + 1))
            .ToDictionary(x => x.Id, x => x.Index);

        var schedule = evaluator.Evaluate(input, [matrixCandidates[0]], matrix, matrixCandidates, candidateMatrixIndices, CancellationToken.None);
        schedule.Should().NotBeNull();

        // Corrupt sequence number to 5 instead of 1
        var corruptedItems = schedule!.Plan.Items
            .Select(it => it with { SequenceNo = 5 })
            .ToArray();
        var corruptedPlan = schedule.Plan with { Items = corruptedItems };

        var act = () => _validator.Validate(input, matrix, matrixCandidates, corruptedPlan);
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*contiguous*");
    }

    [Fact]
    public void Validate_ThrowsWhenMandatoryPoiIsMissing()
    {
        var (input, matrix) = OptionalRouteOptimizationScenarios.CreateZigzagScenario();
        var evaluator = new ItineraryScheduleEvaluator();
        var matrixCandidates = input.Candidates.ToArray();
        var candidateMatrixIndices = matrixCandidates
            .Select((c, i) => (c.Id, Index: i + 1))
            .ToDictionary(x => x.Id, x => x.Index);

        // Sequence includes only M1 (101), missing M2 (102)
        var schedule = evaluator.Evaluate(input, [matrixCandidates[0]], matrix, matrixCandidates, candidateMatrixIndices, CancellationToken.None);
        schedule.Should().NotBeNull();

        var act = () => _validator.Validate(input, matrix, matrixCandidates, schedule!.Plan);
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*mandatory POI ID is missing*");
    }

    [Fact]
    public void Validate_ThrowsWhenMandatoryMetadataMismatches()
    {
        var (input, matrix) = OptionalRouteOptimizationScenarios.CreateZigzagScenario();
        var evaluator = new ItineraryScheduleEvaluator();
        var matrixCandidates = input.Candidates.ToArray();
        var candidateMatrixIndices = matrixCandidates
            .Select((c, i) => (c.Id, Index: i + 1))
            .ToDictionary(x => x.Id, x => x.Index);

        var schedule = evaluator.Evaluate(input, [matrixCandidates[0], matrixCandidates[1]], matrix, matrixCandidates, candidateMatrixIndices, CancellationToken.None);
        schedule.Should().NotBeNull();

        // Corrupt mandatory flag: mark mandatory POI as optional
        var corruptedItems = schedule!.Plan.Items
            .Select(it => it.PointOfInterestId == 101L ? it with { IsMandatory = false } : it)
            .ToArray();
        var corruptedPlan = schedule.Plan with { Items = corruptedItems };

        var act = () => _validator.Validate(input, matrix, matrixCandidates, corruptedPlan);
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*IsMandatory*");
    }

    [Fact]
    public void Validate_ThrowsWhenVisitIsDuplicated()
    {
        var (input, matrix, schedule) = CreateTwoVisitSchedule();
        var duplicate = schedule.Plan.Items
            .First(item => item.Kind == ItineraryItemKind.Visit) with
        {
            SequenceNo = schedule.Plan.Items.Count + 1,
        };
        var corruptedPlan = schedule.Plan with
        {
            Items = [.. schedule.Plan.Items, duplicate],
        };

        var act = () => _validator.Validate(input, matrix, input.Candidates.ToArray(), corruptedPlan);
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*duplicate visit*");
    }

    [Fact]
    public void Validate_ThrowsWhenWholeTimelineIsDelayed()
    {
        var (input, matrix, schedule) = CreateTwoVisitSchedule();
        var corruptedPlan = schedule.Plan with
        {
            Items = schedule.Plan.Items
                .Select(item => item with
                {
                    PlannedArrivalUtc = item.PlannedArrivalUtc.AddMinutes(5),
                    PlannedDepartureUtc = item.PlannedDepartureUtc.AddMinutes(5),
                })
                .ToArray(),
            EndAtUtc = schedule.Plan.EndAtUtc.AddMinutes(5),
            TotalDurationMinutes = schedule.Plan.TotalDurationMinutes + 5,
        };

        var act = () => _validator.Validate(input, matrix, input.Candidates.ToArray(), corruptedPlan);
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*deterministic travel*");
    }

    [Fact]
    public void Validate_ThrowsWhenReportedEndIsMutated()
    {
        var (input, matrix, schedule) = CreateTwoVisitSchedule();
        var corruptedPlan = schedule.Plan with
        {
            EndAtUtc = schedule.Plan.EndAtUtc.AddMinutes(1),
            TotalDurationMinutes = schedule.Plan.TotalDurationMinutes + 1,
        };

        var act = () => _validator.Validate(input, matrix, input.Candidates.ToArray(), corruptedPlan);
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*end time*");
    }

    [Fact]
    public void Validate_ThrowsWhenBudgetIsExceeded()
    {
        var (input, matrix) = OptionalRouteOptimizationScenarios.CreateZigzagScenario();
        var evaluator = new ItineraryScheduleEvaluator();
        var matrixCandidates = input.Candidates.ToArray();
        var candidateMatrixIndices = matrixCandidates
            .Select((c, i) => (c.Id, Index: i + 1))
            .ToDictionary(x => x.Id, x => x.Index);

        var schedule = evaluator.Evaluate(input, [matrixCandidates[0], matrixCandidates[1]], matrix, matrixCandidates, candidateMatrixIndices, CancellationToken.None);
        schedule.Should().NotBeNull();

        // Set input budget lower than the schedule's total cost
        var tightInput = input with { BudgetVnd = 10_000m };

        var act = () => _validator.Validate(tightInput, matrix, matrixCandidates, schedule!.Plan);
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*budget*");
    }

    [Fact]
    public void Validate_ThrowsWhenTotalCostDoesNotMatchSumOfItems()
    {
        var (input, matrix) = OptionalRouteOptimizationScenarios.CreateZigzagScenario();
        var evaluator = new ItineraryScheduleEvaluator();
        var matrixCandidates = input.Candidates.ToArray();
        var candidateMatrixIndices = matrixCandidates
            .Select((c, i) => (c.Id, Index: i + 1))
            .ToDictionary(x => x.Id, x => x.Index);

        var schedule = evaluator.Evaluate(input, [matrixCandidates[0], matrixCandidates[1]], matrix, matrixCandidates, candidateMatrixIndices, CancellationToken.None);
        schedule.Should().NotBeNull();

        var corruptedPlan = schedule!.Plan with { TotalEstimatedCost = 999_999m };

        var act = () => _validator.Validate(input, matrix, matrixCandidates, corruptedPlan);
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*TotalEstimatedCost*");
    }

    [Fact]
    public void Validate_ThrowsWhenItemAndTotalCostsAreMutatedTogether()
    {
        var (input, matrix) = OptionalRouteOptimizationScenarios.CreateZigzagScenario();
        var evaluator = new ItineraryScheduleEvaluator();
        var matrixCandidates = input.Candidates.ToArray();
        var candidateMatrixIndices = matrixCandidates
            .Select((candidate, index) => (candidate.Id, Index: index + 1))
            .ToDictionary(item => item.Id, item => item.Index);
        var schedule = evaluator.Evaluate(
            input,
            [matrixCandidates[0], matrixCandidates[1]],
            matrix,
            matrixCandidates,
            candidateMatrixIndices,
            CancellationToken.None);
        schedule.Should().NotBeNull();

        var corruptedItems = schedule!.Plan.Items
            .Select(item => item.Kind == ItineraryItemKind.Visit
                ? item with { EstimatedCost = 0m }
                : item)
            .ToArray();
        var corruptedPlan = schedule.Plan with
        {
            Items = corruptedItems,
            TotalEstimatedCost = 0m,
        };

        var act = () => _validator.Validate(input, matrix, matrixCandidates, corruptedPlan);
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*candidate cost*");
    }

    [Fact]
    public void Validate_ThrowsWhenRequiredRestIsMissing()
    {
        var (input, matrix, schedule) = CreateFrequentRestSchedule();
        var withoutRest = schedule.Plan.Items
            .Where(item => item.Kind != ItineraryItemKind.Rest)
            .ToArray();
        var corruptedPlan = schedule.Plan with { Items = withoutRest };

        var act = () => _validator.Validate(input, matrix, input.Candidates.ToArray(), corruptedPlan);
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*required rest is missing*");
    }

    [Fact]
    public void Validate_ThrowsWhenRestIsPresentForNonePreference()
    {
        var (input, matrix, schedule) = CreateFrequentRestSchedule();
        var noRestInput = input with { RestPreference = RestPreference.None };

        var act = () => _validator.Validate(noRestInput, matrix, input.Candidates.ToArray(), schedule.Plan);
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*extra or misplaced*");
    }

    [Fact]
    public void Validate_ThrowsWhenTravelDurationToNextMismatches()
    {
        var (input, matrix) = OptionalRouteOptimizationScenarios.CreateZigzagScenario();
        var evaluator = new ItineraryScheduleEvaluator();
        var matrixCandidates = input.Candidates.ToArray();
        var candidateMatrixIndices = matrixCandidates
            .Select((c, i) => (c.Id, Index: i + 1))
            .ToDictionary(x => x.Id, x => x.Index);

        var schedule = evaluator.Evaluate(input, [matrixCandidates[0], matrixCandidates[1]], matrix, matrixCandidates, candidateMatrixIndices, CancellationToken.None);
        schedule.Should().NotBeNull();

        var corruptedItems = schedule!.Plan.Items
            .Select((it, idx) => idx == 0 ? it with { TravelDurationToNextMinutes = 999 } : it)
            .ToArray();
        var corruptedPlan = schedule.Plan with { Items = corruptedItems };

        var act = () => _validator.Validate(input, matrix, matrixCandidates, corruptedPlan);
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*TravelDurationToNextMinutes*");
    }

    [Fact]
    public void Validate_ThrowsWhenOpeningHoursAreViolated()
    {
        var (input, matrix) = OptionalRouteOptimizationScenarios.CreateZigzagScenario();
        var evaluator = new ItineraryScheduleEvaluator();
        var matrixCandidates = input.Candidates.ToArray();
        var candidateMatrixIndices = matrixCandidates
            .Select((c, i) => (c.Id, Index: i + 1))
            .ToDictionary(x => x.Id, x => x.Index);

        var schedule = evaluator.Evaluate(input, [matrixCandidates[0], matrixCandidates[1]], matrix, matrixCandidates, candidateMatrixIndices, CancellationToken.None);
        schedule.Should().NotBeNull();

        // Alter candidate catalog so that POI 101 closes at 06:30 AM (before arrival)
        var alteredCandidates = matrixCandidates
            .Select(c => c.Id == 101L ? c with
            {
                OpeningHours = [new GenerationOpeningHours(2, new TimeOnly(6, 0), new TimeOnly(6, 30))]
            } : c)
            .ToArray();

        var act = () => _validator.Validate(input, matrix, alteredCandidates, schedule!.Plan);
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*opening hours*");
    }

    private static (
        GenerationInput Input,
        RouteDurationMatrix Matrix,
        EvaluatedItinerarySchedule Schedule) CreateFrequentRestSchedule()
    {
        var candidate = OptionalRouteOptimizationScenarios.CreateCandidate(
            101,
            "Long visit",
            visitDurationMinutes: 130,
            cost: 10_000m,
            effectiveDesirabilityScore: 1m);
        var input = OptionalRouteOptimizationScenarios.CreateInput(
            availableMinutes: 300,
            candidates: [candidate],
            mandatoryPoiIds: [candidate.Id],
            restPreference: RestPreference.Frequent);
        var matrix = RouteDurationMatrix.Create(new int[,]
        {
            { 0, 5, 10 },
            { 5, 0, 5 },
            { 10, 5, 0 },
        });
        var schedule = new ItineraryScheduleEvaluator().Evaluate(
            input,
            [candidate],
            matrix,
            [candidate],
            new Dictionary<long, int> { [candidate.Id] = 1 },
            CancellationToken.None);
        schedule.Should().NotBeNull();
        schedule!.Plan.Items.Should().Contain(item => item.Kind == ItineraryItemKind.Rest);
        return (input, matrix, schedule);
    }

    private static (
        GenerationInput Input,
        RouteDurationMatrix Matrix,
        EvaluatedItinerarySchedule Schedule) CreateTwoVisitSchedule()
    {
        var (input, matrix) = OptionalRouteOptimizationScenarios.CreateZigzagScenario();
        var candidates = input.Candidates.ToArray();
        var candidateMatrixIndices = candidates
            .Select((candidate, index) => (candidate.Id, Index: index + 1))
            .ToDictionary(item => item.Id, item => item.Index);
        var sequence = input.MandatoryPoiIds
            .Select(id => candidates.Single(candidate => candidate.Id == id))
            .ToArray();
        var schedule = new ItineraryScheduleEvaluator().Evaluate(
            input,
            sequence,
            matrix,
            candidates,
            candidateMatrixIndices,
            CancellationToken.None);
        schedule.Should().NotBeNull();
        return (input, matrix, schedule!);
    }
}