using System.Diagnostics;

using TripMate.Application.Features.Scheduling.Common;
using TripMate.Application.Features.Scheduling.Csp;
using TripMate.Application.Features.Scheduling.Csp.Objectives;
using TripMate.Application.Features.Scheduling.Routing;

namespace TripMate.Application.UnitTests.Features.Scheduling.Golden.Weighted;

internal static class WeightedFixtures
{
    private static readonly SchedulingGenerationOptions Options = new();

    public static IEnumerable<object[]> WeightedScenarios =>
        WeightedScenarioFactory.CreateAllScenarios().Select(s => new object[] { s.Id });

    public static (WeightedUtilityKernel Kernel, RoutingContext Context, WeightedObjectiveInputs Inputs) InitializedKernel(
        string scenarioId,
        bool disableBound = false,
        BoundVariant variant = BoundVariant.Correct)
    {
        var scenario = WeightedScenarioFactory.CreateAllScenarios().First(s => s.Id == scenarioId);
        var cspOptions = scenario.CspOptions ?? new CspOptions { Mode = ObjectiveMode.WeightedUtility };
        var ctx = CspItinerarySolver.CreatePrimaryContext(scenario.Input, scenario.Matrix, scenario.Candidates, scenario.Indices, Options);
        var reqInputs = WeightedGoldenInputsFactory.CreateRequestInputs(scenario.Candidates);
        var weightedInputs = ObjectiveKernelFactory.BuildWeightedInputs(ctx, reqInputs);
        var rule = CspProductRule.BuildRankRuleFromBase(ctx, weightedInputs.Base);

        using var deadline = new CspItinerarySolver.SolveDeadline(Stopwatch.StartNew(), 10_000, CancellationToken.None);
        var budget = new CspItinerarySolver.SearchBudget(10_000);
        var kernel = new WeightedUtilityKernel(weightedInputs, cspOptions, disableBound, variant);

        _ = new CspItinerarySolver.SearchRun(
            ctx, rule, cspOptions, deadline, budget, false,
            (c, s, st) => null, CancellationToken.None, weightedInputs, kernel);

        return (kernel, ctx, weightedInputs);
    }
}