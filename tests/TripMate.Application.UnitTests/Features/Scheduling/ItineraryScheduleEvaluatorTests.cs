using FluentAssertions;

using TripMate.Application.Features.Scheduling.Common;
using TripMate.Application.Features.Scheduling.Routing;
using TripMate.Application.UnitTests.Features.Scheduling.Fixtures;
using TripMate.Domain.Constants;
using TripMate.Domain.Enums;

namespace TripMate.Application.UnitTests.Features.Scheduling;

public sealed class ItineraryScheduleEvaluatorTests
{
    private readonly ItineraryScheduleEvaluator _evaluator = new();

    [Fact]
    public void Evaluate_WithValidSequence_CalculatesConsistentTimelineAndObjective()
    {
        var (input, matrix) = OptionalRouteOptimizationScenarios.CreateZigzagScenario();
        var matrixCandidates = input.Candidates.ToArray();
        var indexMap = matrixCandidates.Select((c, i) => new { c.Id, Index = i + 1 }).ToDictionary(x => x.Id, x => x.Index);

        var sequence = new[] { matrixCandidates[0], matrixCandidates[1] }; // 101, 102

        var evaluated = _evaluator.Evaluate(input, sequence, matrix, matrixCandidates, indexMap);

        evaluated.Should().NotBeNull();
        evaluated!.VisitPoiIds.Should().Equal(101L, 102L);
        evaluated.Plan.Items.Where(it => it.Kind == ItineraryItemKind.Visit).Select(it => it.PointOfInterestId).Should().Equal(101L, 102L);
        evaluated.TotalMatrixTravelMinutes.Should().Be(matrix.GetMinutes(0, 1) + matrix.GetMinutes(1, 2) + matrix.GetMinutes(2, 5));
        evaluated.TotalEstimatedCost.Should().Be(40_000m);
    }

    [Fact]
    public void Evaluate_WhenEndBeyondHorizon_ReturnsNull()
    {
        var (input, matrix) = OptionalRouteOptimizationScenarios.CreateZigzagScenario(availableMinutes: 60); // 60 minutes is not enough for 101 + 102
        var matrixCandidates = input.Candidates.ToArray();
        var indexMap = matrixCandidates.Select((c, i) => new { c.Id, Index = i + 1 }).ToDictionary(x => x.Id, x => x.Index);

        var sequence = new[] { matrixCandidates[0], matrixCandidates[1] };

        var evaluated = _evaluator.Evaluate(input, sequence, matrix, matrixCandidates, indexMap);

        evaluated.Should().BeNull();
    }

    [Fact]
    public void Evaluate_WhenCostExceedsBudget_ReturnsNull()
    {
        var (input, matrix) = OptionalRouteOptimizationScenarios.CreateZigzagScenario();
        var inputWithTightBudget = input with { BudgetVnd = 25_000m }; // Each mandatory POI costs 20_000m, total 40_000m > 25_000m
        var matrixCandidates = input.Candidates.ToArray();
        var indexMap = matrixCandidates.Select((c, i) => new { c.Id, Index = i + 1 }).ToDictionary(x => x.Id, x => x.Index);

        var sequence = new[] { matrixCandidates[0], matrixCandidates[1] };

        var evaluated = _evaluator.Evaluate(inputWithTightBudget, sequence, matrix, matrixCandidates, indexMap);

        evaluated.Should().BeNull();
    }

    [Fact]
    public void Evaluate_WhenOpeningHoursViolated_ReturnsNull()
    {
        var closedCandidate = OptionalRouteOptimizationScenarios.CreateCandidate(
            id: 999,
            name: "Closed",
            visitDurationMinutes: 60,
            cost: 10_000m,
            effectiveDesirabilityScore: 0.8m,
            openingHours: [new GenerationOpeningHours(2, new TimeOnly(1, 0), new TimeOnly(1, 30))]); // Closed by arrival time

        var (input, matrix) = OptionalRouteOptimizationScenarios.CreateZigzagScenario();
        var candidatesWithClosed = input.Candidates.Append(closedCandidate).ToArray();
        var matrixWithClosed = RouteDurationMatrix.Create(new int[matrix.PointCount + 1, matrix.PointCount + 1]);
        var indexMap = candidatesWithClosed.Select((c, i) => new { c.Id, Index = i + 1 }).ToDictionary(x => x.Id, x => x.Index);

        var sequence = new[] { closedCandidate };

        var evaluated = _evaluator.Evaluate(input, sequence, matrixWithClosed, candidatesWithClosed, indexMap);

        evaluated.Should().BeNull();
    }

    [Fact]
    public void Evaluate_WhenCancelled_ThrowsOperationCanceledException()
    {
        var (input, matrix) = OptionalRouteOptimizationScenarios.CreateZigzagScenario();
        var matrixCandidates = input.Candidates.ToArray();
        var indexMap = matrixCandidates.Select((c, i) => new { c.Id, Index = i + 1 }).ToDictionary(x => x.Id, x => x.Index);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var act = () => _evaluator.Evaluate(input, [matrixCandidates[0]], matrix, matrixCandidates, indexMap, cts.Token);

        act.Should().Throw<OperationCanceledException>();
    }

    [Theory]
    [InlineData("Food & Drink")]
    [InlineData("food & drink")]
    [InlineData("FOOD & DRINK")]
    [InlineData("cafe")]
    [InlineData("restaurant")]
    [InlineData("coffee shop")]
    public void IsRestCategory_WithFoodAndDrinkOrLegacyAliases_ReturnsTrue(string category)
    {
        ItineraryScheduleEvaluator.IsRestCategory(category, hasShelter: false).Should().BeTrue();
    }

    [Fact]
    public void RoutingContext_WhenFoodAndDrinkPoiIsNotMandatory_ExcludesItFromNodes()
    {
        var (input, _) = OptionalRouteOptimizationScenarios.CreateZigzagScenario();
        var foodCandidate = OptionalRouteOptimizationScenarios.CreateCandidate(999, "Quan An", 45, 50_000m, 0.9m) with
        {
            CategoryName = PoiCategoryNames.FoodAndDrink
        };
        var candidates = input.Candidates.Append(foodCandidate).ToArray();
        var indexMap = candidates.Select((c, i) => new { c.Id, Index = i + 1 }).ToDictionary(x => x.Id, x => x.Index);
        var matrixWithFood = RouteDurationMatrix.Create(new int[candidates.Length + 2, candidates.Length + 2]);

        var options = new SchedulingGenerationOptions();
        var ctx = RoutingContext.Create(input, matrixWithFood, candidates, indexMap, options, options.MiniRouting);

        ctx.Nodes.Select(n => n.Candidate.Id).Should().NotContain(999L);
    }

    [Fact]
    public void RoutingContext_WhenFoodAndDrinkPoiIsMandatory_IncludesItInNodes()
    {
        var (input, _) = OptionalRouteOptimizationScenarios.CreateZigzagScenario();
        var foodCandidate = OptionalRouteOptimizationScenarios.CreateCandidate(999, "Quan An", 45, 50_000m, 0.9m) with
        {
            CategoryName = PoiCategoryNames.FoodAndDrink
        };
        var candidates = input.Candidates.Append(foodCandidate).ToArray();
        var indexMap = candidates.Select((c, i) => new { c.Id, Index = i + 1 }).ToDictionary(x => x.Id, x => x.Index);
        var matrixWithFood = RouteDurationMatrix.Create(new int[candidates.Length + 2, candidates.Length + 2]);
        var inputWithMandatoryFood = input with
        {
            MandatoryPoiIds = input.MandatoryPoiIds.Append(999L).ToArray()
        };

        var options = new SchedulingGenerationOptions();
        var ctx = RoutingContext.Create(inputWithMandatoryFood, matrixWithFood, candidates, indexMap, options, options.MiniRouting);

        ctx.Nodes.Select(n => n.Candidate.Id).Should().Contain(999L);
        ctx.Nodes.Single(n => n.Candidate.Id == 999L).IsMandatory.Should().BeTrue();
    }

    [Fact]
    public void Evaluate_WhenRestNeeded_SelectsFoodAndDrinkCandidateAsRestStop()
    {
        var (baseInput, _) = OptionalRouteOptimizationScenarios.CreateZigzagScenario();
        var c101 = OptionalRouteOptimizationScenarios.CreateCandidate(101, "Stop 1", visitDurationMinutes: 160, cost: 20_000m, effectiveDesirabilityScore: 1.0m);
        var c102 = OptionalRouteOptimizationScenarios.CreateCandidate(102, "Stop 2", visitDurationMinutes: 60, cost: 20_000m, effectiveDesirabilityScore: 1.0m);
        var restCandidate = OptionalRouteOptimizationScenarios.CreateCandidate(888, "Rest Stop", visitDurationMinutes: 30, cost: 20_000m, effectiveDesirabilityScore: 0.5m) with
        {
            CategoryName = PoiCategoryNames.FoodAndDrink
        };
        var candidates = new[] { c101, c102, restCandidate };
        var indexMap = candidates.Select((c, i) => new { c.Id, Index = i + 1 }).ToDictionary(x => x.Id, x => x.Index);
        var matrix = RouteDurationMatrix.Create(new int[candidates.Length + 2, candidates.Length + 2]);

        var inputWithRest = baseInput with
        {
            AvailableMinutes = 600,
            Candidates = candidates,
            MandatoryPoiIds = [101L, 102L],
            RestPreference = RestPreference.Auto
        };

        var sequence = new[] { c101, c102 };
        var evaluated = _evaluator.Evaluate(inputWithRest, sequence, matrix, candidates, indexMap);

        evaluated.Should().NotBeNull();
        evaluated!.Plan.Items.Should().Contain(it => it.Kind == ItineraryItemKind.Rest && it.PointOfInterestId == 888L);
    }
}