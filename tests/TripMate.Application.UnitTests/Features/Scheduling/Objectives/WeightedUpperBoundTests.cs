using TripMate.Application.Features.Scheduling.Common;
using TripMate.Application.Features.Scheduling.Csp;
using TripMate.Application.Features.Scheduling.Csp.Objectives;
using TripMate.Application.Features.Scheduling.Personalization;
using TripMate.Application.Features.Scheduling.TimeOfDay;
using TripMate.Application.UnitTests.Features.Scheduling.Golden.Weighted;
using Xunit;

namespace TripMate.Application.UnitTests.Features.Scheduling.Objectives;

public class WeightedUpperBoundTests
{
    public static IEnumerable<object[]> WeightedScenarios => WeightedFixtures.WeightedScenarios;

    [Theory]
    [MemberData(nameof(WeightedScenarios))]
    public void UMax_dominates_every_feasible_gain(string id)
    {
        var (kernel, ctx, inputs) = WeightedFixtures.InitializedKernel(id);
        const double scale = WeightedObjectiveInputs.Scale;

        for (var v = 0; v < ctx.Nodes.Count; v++)
        {
            foreach (var (open, close) in ctx.OpenWindows(v))
            {
                for (var start = open; start + ctx.Nodes[v].VisitMinutes <= close; start += 5)
                {
                    var gain = scale * inputs.Base[v] * inputs.TimeFit.Factor(v, start, start + ctx.Nodes[v].VisitMinutes);
                    Assert.True(gain <= kernel.UMaxForTest[v], $"{id} node {v} start {start}: {gain} > {kernel.UMaxForTest[v]}");
                }
            }
        }
    }

    [Theory]
    [MemberData(nameof(WeightedScenarios))]
    public void UMax_dominates_every_feasible_gain_with_real_TimeFit(string id)
    {
        var scenario = WeightedScenarioFactory.CreateAllScenarios().First(s => s.Id == id);
        var cspOptions = scenario.CspOptions ?? new CspOptions { Mode = ObjectiveMode.WeightedUtility };
        var ctx = CspItinerarySolver.CreatePrimaryContext(scenario.Input, scenario.Matrix, scenario.Candidates, scenario.Indices, new SchedulingGenerationOptions());
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
            ConfigVersion: "test-real-timefit");

        var baseByPoiId = scenario.Candidates.ToDictionary(c => c.Id, c => WeightedGoldenInputsFactory.CalculateBaseScore(c.Id));
        var reqInputs = new WeightedRequestInputs(baseByPoiId, personalParams, new RealTimeFitProviderFactory(personalParams));
        var weightedInputs = ObjectiveKernelFactory.BuildWeightedInputs(ctx, reqInputs);
        var rule = CspProductRule.BuildRankRuleFromBase(ctx, weightedInputs.Base);

        using var deadline = new CspItinerarySolver.SolveDeadline(System.Diagnostics.Stopwatch.StartNew(), 10_000, CancellationToken.None);
        var budget = new CspItinerarySolver.SearchBudget(10_000);
        var kernel = new WeightedUtilityKernel(weightedInputs, cspOptions, disableBound: false, BoundVariant.Correct);

        _ = new CspItinerarySolver.SearchRun(
            ctx, rule, cspOptions, deadline, budget, false,
            (c, s, st) => null, CancellationToken.None, weightedInputs, kernel);

        const double scale = WeightedObjectiveInputs.Scale;

        for (var v = 0; v < ctx.Nodes.Count; v++)
        {
            foreach (var (open, close) in ctx.OpenWindows(v))
            {
                for (var start = open; start + ctx.Nodes[v].VisitMinutes <= close; start += 5)
                {
                    var gain = scale * weightedInputs.Base[v] * weightedInputs.TimeFit.Factor(v, start, start + ctx.Nodes[v].VisitMinutes);
                    Assert.True(gain <= kernel.UMaxForTest[v], $"{id} node {v} start {start}: gain {gain} > UMax {kernel.UMaxForTest[v]}");
                }
            }
        }
    }

    [Fact]
    public void RoundingSlack_Guarantees_Upper_Bound_Dominates_True_Sum_When_Half_Integers()
    {
        var rng = new Random(42);
        var naiveUnderestimateCount = 0;

        for (var trial = 0; trial < 1000; trial++)
        {
            var k = rng.Next(1, 10); // number of stops
            var trueSum = 0L;
            var sumUMax = 0L;
            var totalCostScaled = 0.0;

            for (var i = 0; i < k; i++)
            {
                var integerPart = rng.Next(10, 500) * 2 + 1; // odd integer: integerPart + 0.5 rounds UP
                var cost = rng.Next(1, 50) + 0.25;
                var net = integerPart + 0.5;
                var gain = net + cost;

                var visitTerm = (long)Math.Round(net, MidpointRounding.ToEven);
                trueSum += visitTerm;

                sumUMax += (long)Math.Ceiling(gain);
                totalCostScaled += cost;
            }

            var travelLb = (long)Math.Floor(totalCostScaled);
            var naiveUb = sumUMax - travelLb;
            var slack = k + 1;
            var safeUb = naiveUb + slack;

            if (naiveUb < trueSum)
            {
                naiveUnderestimateCount++;
            }

            Assert.True(safeUb >= trueSum, $"Safe UB {safeUb} must be >= trueSum {trueSum}");
        }

        // Khẳng định rằng nếu không có slack, cận trên thực sự bị thấp hơn tổng thật ở một số trường hợp!
        Assert.True(naiveUnderestimateCount > 0, "Naive UB without slack must underestimate at least once on odd half-integers.");
    }
}
