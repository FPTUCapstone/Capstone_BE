using FluentAssertions;

using TripMate.Application.Features.Scheduling.Common;
using TripMate.Application.Features.Scheduling.Csp;
using TripMate.Application.Features.Scheduling.Csp.Objectives;
using TripMate.Application.UnitTests.Features.Scheduling.Golden;
using TripMate.Application.UnitTests.Features.Scheduling.Golden.Weighted;

using Xunit;

namespace TripMate.Application.UnitTests.Features.Scheduling.Golden.WeightedTimeFit;

public class WeightedTimeFitSolverDeterminismTests
{
    private static readonly SchedulingGenerationOptions Options = new();
    private static readonly CspOptions GoldenCspOptions = new() { TimeLimitMilliseconds = 120_000 };

    [Theory]
    [InlineData("grid-c10-m1-none-s1")]
    [InlineData("named-zigzag")]
    [InlineData("incumbent-valid-c20-m2-none-s10")]
    [InlineData("boundary-fractional-seconds-s41")]
    public void WeightedTimeFitSolver_20Iterations_ProducesBitForBitIdenticalSnapshots(string scenarioId)
    {
        var allScenarios = GoldenScenarioFactory.CreateAllScenarios();
        var scenario = allScenarios.First(s => s.Id == scenarioId);

        var primaryCtx = CspItinerarySolver.CreatePrimaryContext(
            scenario.Input, scenario.Matrix, scenario.Candidates, scenario.Indices, Options);
        var requestInputs = WeightedTimeFitInputsFactory.CreateRequestInputs(scenario.Candidates);
        var inputs = ObjectiveKernelFactory.BuildWeightedInputs(primaryCtx, requestInputs);
        var nodeOf = primaryCtx.Nodes.Select((n, i) => (n.Candidate.Id, Index: i)).ToDictionary(x => x.Id, x => x.Index);

        var baseCspOpts = scenario.CspOptions ?? GoldenCspOptions;
        var cspOptions = new CspOptions
        {
            Mode = ObjectiveMode.WeightedUtility,
            TimeLimitMilliseconds = baseCspOpts.TimeLimitMilliseconds,
            MaxNodes = baseCspOpts.MaxNodes,
            MaxStops = baseCspOpts.MaxStops,
            MaxOptionalDomainSize = baseCspOpts.MaxOptionalDomainSize,
            FinalCandidatesToValidate = baseCspOpts.FinalCandidatesToValidate,
            PolishWithLocalSearch = false
        };

        var evaluator = new ItineraryScheduleEvaluator(Options);

        WeightedSolverGoldenSnapshot? baseline = null;

        for (var iter = 0; iter < 20; iter++)
        {
            var solver = new CspItinerarySolver(evaluator, Options, cspOptions);
            var result = solver.Solve(
                scenario.Input,
                scenario.Matrix,
                scenario.Candidates,
                scenario.Indices,
                scenario.InitialIncumbent,
                weighted: requestInputs,
                cancellationToken: CancellationToken.None);

            var snapshot = WeightedSnapshotMapper.From(scenario, result, primaryCtx, inputs, nodeOf);

            if (iter == 0)
            {
                baseline = snapshot;
            }
            else
            {
                snapshot.Should().BeEquivalentTo(
                    baseline,
                    $"Iteration {iter} of scenario {scenarioId} must be bit-for-bit identical to baseline iteration 0");
            }
        }
    }
}