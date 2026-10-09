using FluentAssertions;

using TripMate.Application.Features.Scheduling.Common;
using TripMate.Application.UnitTests.Features.Scheduling.Fixtures;
using TripMate.Domain.Enums;

namespace TripMate.Application.UnitTests.Features.Scheduling;

public sealed class ProductRulePlanComparisonTests
{
    // Zigzag fixture: mandatory 101 and 102; optional 201 (desirability 0.95) ranks above 202 (0.85).
    private static readonly (GenerationInput Input, RouteDurationMatrix Matrix) Zigzag =
        OptionalRouteOptimizationScenarios.CreateZigzagScenario();

    [Fact]
    public void HigherRankedOptional_BeatsLowerRankedOptionalEvenWithMoreTravel()
    {
        var withTopRanked = Plan(101, 102, 201);
        var withLowerRanked = Plan(101, 102, 202);

        Compare(withTopRanked, withLowerRanked).Should().BeNegative();
        Compare(withLowerRanked, withTopRanked).Should().BePositive();
    }

    [Fact]
    public void AdditionalOptional_BeatsTheSameSetWithoutIt()
    {
        Compare(Plan(101, 201, 102, 202), Plan(101, 201, 102)).Should().BeNegative();
    }

    [Fact]
    public void SameVisits_LessMatrixTravelIncludingTheFinalLegWins()
    {
        // 0→1→3→2→4→End = 10+5+52+6+8 = 81 minutes; 0→1→2→3→4→End = 10+50+52+55+8 = 175 minutes.
        var shortRoute = Plan(101, 201, 102, 202);
        var zigzagRoute = Plan(101, 102, 201, 202);

        ProductRulePlanComparison.MatrixTravelMinutes(Zigzag.Input, Zigzag.Matrix, shortRoute).Should().Be(81);
        ProductRulePlanComparison.MatrixTravelMinutes(Zigzag.Input, Zigzag.Matrix, zigzagRoute).Should().Be(175);
        Compare(shortRoute, zigzagRoute).Should().BeNegative();
    }

    [Fact]
    public void IdenticalPlans_AreEqual()
    {
        Compare(Plan(101, 201, 102, 202), Plan(101, 201, 102, 202)).Should().Be(0);
    }

    [Theory]
    [InlineData(10, 0, 1)]
    [InlineData(20, 3, 2)]
    [InlineData(40, 6, 3)]
    public async Task OptimizedHeuristic_IsNeverWorseThanTheUnoptimizedPlan(int candidates, int mandatory, int seed)
    {
        var (input, matrix) = OptionalRouteOptimizationScenarios.CreateSyntheticCorpusScenario(candidates, mandatory, seed);

        var disabled = await Generate(input, matrix, enableOptimization: false);
        var enabled = await Generate(input, matrix, enableOptimization: true);

        ProductRulePlanComparison.Compare(input, matrix, enabled, disabled).Should().BeLessThanOrEqualTo(0);
    }

    private static int Compare(GeneratedItineraryPlan x, GeneratedItineraryPlan y) =>
        ProductRulePlanComparison.Compare(Zigzag.Input, Zigzag.Matrix, x, y);

    private static async Task<GeneratedItineraryPlan> Generate(
        GenerationInput input,
        RouteDurationMatrix matrix,
        bool enableOptimization)
    {
        var result = await new ItineraryGenerationService(
                new FixedMatrixProvider(matrix),
                options: new SchedulingGenerationOptions { EnableOptionalRouteOptimization = enableOptimization })
            .GenerateAsync(input, CancellationToken.None);
        result.IsSuccess.Should().BeTrue();
        return result.Value;
    }

    private static GeneratedItineraryPlan Plan(params long[] visitIds)
    {
        var start = Zigzag.Input.StartAtUtc;
        var items = visitIds
            .Select((id, index) => new GeneratedItineraryItem(
                index + 1,
                id,
                $"POI {id}",
                ItineraryItemKind.Visit,
                start.AddHours(index),
                start.AddHours(index).AddMinutes(30),
                id < 200,
                0m,
                "Test"))
            .ToArray();
        return new GeneratedItineraryPlan(items, start.AddHours(visitIds.Length), visitIds.Length * 60, 0m);
    }

    private sealed class FixedMatrixProvider(RouteDurationMatrix matrix) : IRouteDurationProvider
    {
        public Task<RouteDurationMatrix> GetMatrixAsync(
            IReadOnlyList<RoutePoint> points,
            TransportMode transportMode,
            CancellationToken cancellationToken) => Task.FromResult(matrix);
    }
}