using FluentAssertions;
using TripMate.Application.Features.Scheduling.Common;
using TripMate.Application.Features.Scheduling.Csp;
using Xunit;

namespace TripMate.Application.UnitTests.Features.Scheduling.Golden;

public class SolverDeterminismTests
{
    private static readonly SchedulingGenerationOptions Options = new();
    private static readonly CspOptions GoldenCspOptions = new() { TimeLimitMilliseconds = 120_000 };

    [Theory]
    [InlineData("grid-c20-m1-none-s1")]
    [InlineData("named-zigzag")]
    [InlineData("tie-c20-m0-frequent-s31")]
    [InlineData("boundary-fractional-seconds-s41")]
    public void Solver_ProducesIdenticalResults_Across20ConsecutiveRuns(string scenarioId)
    {
        var scenario = GoldenScenarioFactory.CreateAllScenarios().Single(s => s.Id == scenarioId);
        SolverGoldenSnapshot? baseline = null;

        for (var run = 0; run < 20; run++)
        {
            var solver = new CspItinerarySolver(
                new ItineraryScheduleEvaluator(Options),
                Options,
                scenario.CspOptions ?? GoldenCspOptions);

            var primaryCtx = CspItinerarySolver.CreatePrimaryContext(
                scenario.Input, scenario.Matrix, scenario.Candidates, scenario.Indices, Options);

            var result = solver.Solve(
                scenario.Input,
                scenario.Matrix,
                scenario.Candidates,
                scenario.Indices,
                scenario.InitialIncumbent);

            var snapshot = SnapshotMapper.From(scenario, result, primaryCtx);

            if (baseline is null)
            {
                baseline = snapshot;
            }
            else
            {
                snapshot.Feasible.Should().Be(baseline.Feasible, $"Lần chạy {run}: Feasible phải giống nhau");
                snapshot.BestVisitPoiIds.Should().Equal(baseline.BestVisitPoiIds, $"Lần chạy {run}: Dãy POI tốt nhất phải giống nhau");
                snapshot.BestTravelMinutes.Should().Be(baseline.BestTravelMinutes, $"Lần chạy {run}: Phút di chuyển phải giống nhau");
                snapshot.NodesExpanded.Should().Be(baseline.NodesExpanded, $"Lần chạy {run}: NodesExpanded phải giống nhau");
                snapshot.SolutionsFound.Should().Be(baseline.SolutionsFound, $"Lần chạy {run}: SolutionsFound phải giống nhau");
                snapshot.PrunedByBound.Should().Be(baseline.PrunedByBound, $"Lần chạy {run}: PrunedByBound phải giống nhau");
                snapshot.EliteVisitIds.Should().BeEquivalentTo(baseline.EliteVisitIds, options => options.WithStrictOrdering(), $"Lần chạy {run}: EliteVisitIds phải giống nhau");
            }
        }
    }
}
