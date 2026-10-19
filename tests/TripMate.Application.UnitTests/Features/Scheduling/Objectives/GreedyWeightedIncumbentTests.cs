using System.Diagnostics;
using TripMate.Application.Features.Scheduling.Common;
using TripMate.Application.Features.Scheduling.Csp;
using TripMate.Application.Features.Scheduling.Csp.Objectives;
using TripMate.Application.Features.Scheduling.Personalization;
using TripMate.Application.Features.Scheduling.Routing;
using TripMate.Application.UnitTests.Features.Scheduling.Golden;
using TripMate.Application.UnitTests.Features.Scheduling.Golden.Weighted;
using Xunit;

namespace TripMate.Application.UnitTests.Features.Scheduling.Objectives;

public class GreedyWeightedIncumbentTests
{
    private static readonly SchedulingGenerationOptions Options = new();

    private static (RoutingContext Context, WeightedObjectiveInputs Inputs) Setup(GoldenScenario scenario)
    {
        var ctx = CspItinerarySolver.CreatePrimaryContext(scenario.Input, scenario.Matrix, scenario.Candidates, scenario.Indices, Options);
        var baseByPoiId = scenario.Candidates.ToDictionary(c => c.Id, c => (double)c.EffectiveDesirabilityScore);
        var personalParams = new PersonalParams(
            LambdaPerMin: 0.015,
            Mu: 0.005,
            MaxStopsPerDay: 5,
            MaxDailyEffort: 300,
            RestAfterMin: 120,
            MaxConsecutiveOutdoor: 3,
            SunriseFactor: 0.6,
            GammaDefault: 0.6,
            GammaLoved: 0.85,
            LovedCategories: new HashSet<string>(),
            RhoOutdoor: 0.25,
            RhoRest: 0.20,
            RhoStops: 0.30,
            RhoEffortPerHour: 0.05,
            ConfigVersion: "test-greedy");

        var requestInputs = new WeightedRequestInputs(baseByPoiId, personalParams, new ConstantTimeFitFactory());
        var weightedInputs = ObjectiveKernelFactory.BuildWeightedInputs(ctx, requestInputs);
        return (ctx, weightedInputs);
    }

    [Fact]
    public void Greedy_includes_all_mandatory_nodes_and_produces_feasible_route()
    {
        var scenarios = WeightedScenarioFactory.CreateAllScenarios().Where(s => s.Input.MandatoryPoiIds.Count > 0).Take(5);
        var cspOptions = new CspOptions { Mode = ObjectiveMode.WeightedUtility };

        foreach (var scenario in scenarios)
        {
            var (ctx, inputs) = Setup(scenario);
            var seq = GreedyWeightedIncumbent.BuildSequence(ctx, inputs, cspOptions);

            Assert.NotEmpty(seq);
            var route = RouteState.Build(ctx, seq);
            Assert.NotNull(route);

            foreach (var m in ctx.MandatoryNodes)
            {
                Assert.Contains(m, seq);
            }
        }
    }

    [Fact]
    public void Greedy_is_strictly_deterministic()
    {
        var scenario = WeightedScenarioFactory.CreateAllScenarios().First(s => s.Id.Contains("c40"));
        var (ctx, inputs) = Setup(scenario);
        var cspOptions = new CspOptions { Mode = ObjectiveMode.WeightedUtility };

        var seq1 = GreedyWeightedIncumbent.BuildSequence(ctx, inputs, cspOptions);
        var seq2 = GreedyWeightedIncumbent.BuildSequence(ctx, inputs, cspOptions);

        Assert.Equal(seq1, seq2);
    }

    [Fact]
    public void Greedy_execution_time_is_under_50ms_at_n40()
    {
        var scenario = WeightedScenarioFactory.CreateAllScenarios().First(s => s.Id.Contains("c40"));
        var (ctx, inputs) = Setup(scenario);
        var cspOptions = new CspOptions { Mode = ObjectiveMode.WeightedUtility };

        // Warmup
        _ = GreedyWeightedIncumbent.BuildSequence(ctx, inputs, cspOptions);

        var sw = Stopwatch.StartNew();
        var seq = GreedyWeightedIncumbent.BuildSequence(ctx, inputs, cspOptions);
        sw.Stop();

        Assert.NotEmpty(seq);
        Assert.True(sw.ElapsedMilliseconds < 50, $"Greedy build took {sw.ElapsedMilliseconds} ms, expected < 50 ms");
    }
}
