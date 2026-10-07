using FluentAssertions;

using TripMate.Application.Features.Scheduling.Common;
using TripMate.Application.UnitTests.Features.Scheduling.Fixtures;
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
}