using TripMate.Application.Features.Scheduling.Common;
using TripMate.Application.Features.Scheduling.Csp;
using TripMate.Application.Features.Scheduling.Csp.Objectives;
using TripMate.Application.Features.Scheduling.Personalization;
using TripMate.Application.Features.Scheduling.Routing;
using TripMate.Application.UnitTests.Features.Scheduling.Fixtures;
using TripMate.Domain.Constants;
using TripMate.Domain.Enums;

using Xunit;

namespace TripMate.Application.UnitTests.Features.Scheduling.Objectives;

public class WeightedTrapTests
{
    private static readonly SchedulingGenerationOptions Options = new();

    private static (CspItinerarySolver.SearchRun Run, WeightedUtilityKernel Kernel, RoutingContext Context) CreateTestRun(
        GenerationInput input,
        RouteDurationMatrix matrix,
        IReadOnlyList<GenerationCandidate> candidates,
        IReadOnlyDictionary<long, int> candidateIndices,
        CspOptions cspOptions,
        WeightedRequestInputs requestInputs,
        BoundVariant variant)
    {
        var ctx = CspItinerarySolver.CreatePrimaryContext(input, matrix, candidates, candidateIndices, Options);
        var weightedInputs = ObjectiveKernelFactory.BuildWeightedInputs(ctx, requestInputs);
        var rule = CspProductRule.BuildRankRuleFromBase(ctx, weightedInputs.Base);

        var kernel = new WeightedUtilityKernel(weightedInputs, cspOptions, disableBound: false, variant: variant);

        var deadline = new CspItinerarySolver.SolveDeadline(System.Diagnostics.Stopwatch.StartNew(), 10_000, CancellationToken.None);
        var budget = new CspItinerarySolver.SearchBudget(10_000);

        var run = new CspItinerarySolver.SearchRun(
            ctx, rule, cspOptions, deadline, budget, false,
            (c, s, st) => null, CancellationToken.None, weightedInputs, kernel);

        return (run, kernel, ctx);
    }

    [Theory]
    [InlineData((int)BoundVariant.Correct, true)]
    [InlineData((int)BoundVariant.OmitMandatoryGain, false)]
    public void MandatoryGainTrap_Prunes_Optimal_When_Mandatory_Gain_Is_Omitted(int variantValue, bool expectedCanImprove)
    {
        var variant = (BoundVariant)variantValue;
        // 2 candidates: C1 (optional, low base), C2 (mandatory, high base)
        var c1 = OptionalRouteOptimizationScenarios.CreateCandidate(101, "C1", visitDurationMinutes: 30, cost: 0, effectiveDesirabilityScore: 0.2m);
        var c2 = OptionalRouteOptimizationScenarios.CreateCandidate(102, "C2", visitDurationMinutes: 30, cost: 0, effectiveDesirabilityScore: 0.9m);

        var candidates = new[] { c1, c2 };
        var indices = new Dictionary<long, int> { [101] = 1, [102] = 2 };
        var input = OptionalRouteOptimizationScenarios.CreateInput(
            availableMinutes: 300,
            candidates,
            mandatoryPoiIds: [102],
            restPreference: RestPreference.None);

        var matrix = RouteDurationMatrix.Create(new int[,]
        {
            { 0, 5, 5, 5 },
            { 5, 0, 5, 5 },
            { 5, 5, 0, 5 },
            { 5, 5, 5, 0 },
        });

        var baseByPoiId = new Dictionary<long, double>
        {
            [101] = 0.20,
            [102] = 0.90,
        };

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
            ConfigVersion: "mandatory-gain-trap");

        var requestInputs = new WeightedRequestInputs(baseByPoiId, personalParams, new ConstantTimeFitFactory());
        var cspOptions = new CspOptions { Mode = ObjectiveMode.WeightedUtility };

        var (run, kernel, ctx) = CreateTestRun(input, matrix, candidates, indices, cspOptions, requestInputs, variant);

        // Seed incumbent with 3,000 (greater than C1 alone ~1,800, but less than C2 ~9,000)
        kernel.SetIncumbentForTest(3000);

        var domain = new[] { 0 };
        var canImprove = kernel.CanImprove(domain, domainCount: 1, depth: 0, departure: 0, current: 0);
        Assert.Equal(expectedCanImprove, canImprove);
    }

    [Theory]
    [InlineData((int)BoundVariant.Correct, true)]
    [InlineData((int)BoundVariant.GreedySlotsOnly, false)]
    public void SlotGreedyTrap_Prunes_Optimal_When_Slots_Bound_Sorts_By_Density_Instead_Of_Value(int variantValue, bool expectedCanImprove)
    {
        var variant = (BoundVariant)variantValue;
        // MaxStops = 1.
        // Node 0: short duration (5 min), low base (0.15) -> high density 1500 / 6 = 250
        // With transition buffer, weights are ~21 for c1 and ~80 for c2.
        // Node 0: short duration (5 min), Base 0.35 -> uMax 3500 -> density 3500 / 21 = 166.7
        // Node 1: long duration (60 min), Base 0.80 -> uMax 8000 -> density 8000 / 80 = 100.0, but value 8000
        var c1 = OptionalRouteOptimizationScenarios.CreateCandidate(101, "QuickLow", visitDurationMinutes: 5, cost: 0, effectiveDesirabilityScore: 0.35m);
        var c2 = OptionalRouteOptimizationScenarios.CreateCandidate(102, "LongHigh", visitDurationMinutes: 60, cost: 0, effectiveDesirabilityScore: 0.80m);

        var candidates = new[] { c1, c2 };
        var indices = new Dictionary<long, int> { [101] = 1, [102] = 2 };
        var input = OptionalRouteOptimizationScenarios.CreateInput(
            availableMinutes: 300,
            candidates,
            mandatoryPoiIds: [],
            restPreference: RestPreference.None);

        var matrix = RouteDurationMatrix.Create(new int[,]
        {
            { 0, 1, 5, 1 },
            { 1, 0, 5, 1 },
            { 5, 5, 0, 5 },
            { 1, 1, 5, 0 },
        });

        var baseByPoiId = new Dictionary<long, double>
        {
            [101] = 0.35,
            [102] = 0.80,
        };

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
            ConfigVersion: "slot-greedy-trap");

        var cspOptions = new CspOptions { Mode = ObjectiveMode.WeightedUtility, MaxStops = 1 };

        var (run, kernel, ctx) = CreateTestRun(input, matrix, candidates, indices, cspOptions, new WeightedRequestInputs(baseByPoiId, personalParams, new ConstantTimeFitFactory()), variant);

        // Seed incumbent with 5,000 (higher than QuickLow ~3,350, but lower than LongHigh ~7,850)
        kernel.SetIncumbentForTest(5000);

        // Domain has both candidates
        var domain = new[] { 0, 1 };
        var canImprove = kernel.CanImprove(domain, domainCount: 2, depth: 0, departure: 0, current: 0);
        Assert.Equal(expectedCanImprove, canImprove);
    }

    [Theory]
    [InlineData((int)BoundVariant.Correct, true)]
    [InlineData((int)BoundVariant.ApplyGamma, false)]
    public void GammaDensityTrap_Prunes_Optimal_When_Gamma_Is_Multiplied_In_Upper_Bound(int variantValue, bool expectedCanImprove)
    {
        var variant = (BoundVariant)variantValue;
        // Category "Culture" with Loved gamma = 0.60
        // Candidate has Base = 0.50 (gain 5,000).
        // If gamma (0.60) is multiplied, uMax becomes 3,000.
        // Incumbent has 3,500.
        var c1 = OptionalRouteOptimizationScenarios.CreateCandidate(101, "C1", visitDurationMinutes: 30, cost: 0, effectiveDesirabilityScore: 0.50m) with
        {
            CategoryName = PoiCategoryNames.Culture
        };

        var candidates = new[] { c1 };
        var indices = new Dictionary<long, int> { [101] = 1 };
        var input = OptionalRouteOptimizationScenarios.CreateInput(
            availableMinutes: 300,
            candidates,
            mandatoryPoiIds: [],
            restPreference: RestPreference.None);

        var matrix = RouteDurationMatrix.Create(new int[,]
        {
            { 0, 1, 1 },
            { 1, 0, 1 },
            { 1, 1, 0 },
        });

        var baseByPoiId = new Dictionary<long, double> { [101] = 0.50 };

        var personalParams = new PersonalParams(
            LambdaPerMin: 0.015,
            Mu: 0.005,
            MaxStopsPerDay: 5,
            MaxDailyEffort: 300,
            RestAfterMin: 120,
            MaxConsecutiveOutdoor: 3,
            SunriseFactor: 0.6,
            GammaDefault: 0.6,
            GammaLoved: 0.60, // 0.60 discount
            LovedCategories: new HashSet<string> { PoiCategoryNames.Culture },
            RhoOutdoor: 0.25,
            RhoRest: 0.20,
            RhoStops: 0.30,
            RhoEffortPerHour: 0.05,
            ConfigVersion: "gamma-trap");

        var cspOptions = new CspOptions { Mode = ObjectiveMode.WeightedUtility };

        var (run, kernel, ctx) = CreateTestRun(input, matrix, candidates, indices, cspOptions, new WeightedRequestInputs(baseByPoiId, personalParams, new ConstantTimeFitFactory()), variant);

        // Seed incumbent with 3,500 (lower than 5,000 without gamma, but higher than 3,000 with gamma)
        kernel.SetIncumbentForTest(3500);

        var domain = new[] { 0 };
        var canImprove = kernel.CanImprove(domain, domainCount: 1, depth: 0, departure: 0, current: 0);

        Assert.Equal(expectedCanImprove, canImprove);
    }

    [Theory]
    [InlineData((int)BoundVariant.Correct, true)]
    [InlineData((int)BoundVariant.NoRoundingSlack, false)]
    public void RoundingTrap_Prunes_Optimal_When_Slack_Is_Omitted_On_Half_Integers(int variantValue, bool expectedCanImprove)
    {
        var variant = (BoundVariant)variantValue;
        // 2 candidates. Each has net = 999.5.
        // Scale * gain = 1000.
        // Scale * travelCost = 0.5.
        // J thật: Round(999.5) + Round(999.5) = 1000 + 1000 = 2000.
        // Cận trên không có slack: Ceil(1000) + Ceil(1000) - Floor(1.0) = 1999 < 2000.
        var c1 = OptionalRouteOptimizationScenarios.CreateCandidate(101, "C1", visitDurationMinutes: 30, cost: 0, effectiveDesirabilityScore: 0.1m);
        var c2 = OptionalRouteOptimizationScenarios.CreateCandidate(102, "C2", visitDurationMinutes: 30, cost: 0, effectiveDesirabilityScore: 0.1m);

        var candidates = new[] { c1, c2 };
        var indices = new Dictionary<long, int> { [101] = 1, [102] = 2 };
        var input = OptionalRouteOptimizationScenarios.CreateInput(
            availableMinutes: 300,
            candidates,
            mandatoryPoiIds: [],
            restPreference: RestPreference.None);

        var matrix = RouteDurationMatrix.Create(new int[,]
        {
            { 0,  5,  5, 10 },
            { 5,  0,  5, 10 },
            { 5,  5,  0, 10 },
            { 10, 10, 10, 0 },
        });

        // Base = 0.1000 => Scale * Base = 1000.
        var baseByPoiId = new Dictionary<long, double>
        {
            [101] = 0.1000,
            [102] = 0.1000,
        };

        var personalParams = new PersonalParams(
            LambdaPerMin: 0.00001, // 0.00001 * 10,000 * 10 = 1.0 travelCost => Floor(1.0) = 1
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
            ConfigVersion: "rounding-trap");

        var cspOptions = new CspOptions { Mode = ObjectiveMode.WeightedUtility };

        var (run, kernel, ctx) = CreateTestRun(input, matrix, candidates, indices, cspOptions, new WeightedRequestInputs(baseByPoiId, personalParams, new ConstantTimeFitFactory()), variant);

        // Incumbent has objective 2,000
        kernel.SetIncumbentForTest(2000);

        var domain = new[] { 0, 1 };
        var canImprove = kernel.CanImprove(domain, domainCount: 2, depth: 0, departure: 0, current: 0);

        Assert.Equal(expectedCanImprove, canImprove);
    }

    public sealed record TrapFixture(
        GenerationInput Input,
        RouteDurationMatrix Matrix,
        IReadOnlyList<GenerationCandidate> Candidates,
        IReadOnlyDictionary<long, int> Indices,
        WeightedRequestInputs RequestInputs,
        CspOptions CspOptions);

    public sealed record TrapSolveResult(
        CspResult Result,
        long? BestModelObjective);

    private static TrapSolveResult Solve(TrapFixture fixture, BoundVariant variant, bool disableBound = false)
    {
        var options = new SchedulingGenerationOptions();
        var cspOptions = new CspOptions
        {
            Mode = ObjectiveMode.WeightedUtility,
            MaxNodes = fixture.CspOptions.MaxNodes,
            MaxStops = fixture.CspOptions.MaxStops,
            MaxOptionalDomainSize = fixture.CspOptions.MaxOptionalDomainSize,
            FinalCandidatesToValidate = fixture.CspOptions.FinalCandidatesToValidate,
            TimeLimitMilliseconds = 10_000,
            PolishWithLocalSearch = false
        };

        var solver = new CspItinerarySolver(new ItineraryScheduleEvaluator(options), options, cspOptions)
        {
            WeightedKernelFactoryForTest = (inputs, kernelOptions) =>
                new WeightedUtilityKernel(inputs, kernelOptions, disableBound, variant)
        };
        var result = solver.Solve(
            fixture.Input,
            fixture.Matrix,
            fixture.Candidates,
            fixture.Indices,
            initialIncumbent: null,
            weighted: fixture.RequestInputs,
            cancellationToken: CancellationToken.None);

        var primaryCtx = CspItinerarySolver.CreatePrimaryContext(fixture.Input, fixture.Matrix, fixture.Candidates, fixture.Indices, options);
        var weightedInputs = ObjectiveKernelFactory.BuildWeightedInputs(primaryCtx, fixture.RequestInputs);
        var nodeOf = primaryCtx.Nodes.Select((n, i) => (n.Candidate.Id, Index: i)).ToDictionary(x => x.Id, x => x.Index);

        long? modelObjective = null;
        if (result.Schedule is { } schedule)
        {
            var visitPoiIds = schedule.VisitPoiIds;
            if (visitPoiIds.Count > 0 && visitPoiIds.All(nodeOf.ContainsKey))
            {
                var seq = visitPoiIds.Select(id => nodeOf[id]).ToArray();
                if (RouteState.Build(primaryCtx, seq) is { } state)
                {
                    var groupScratch = new int[weightedInputs.DiversityGroupCount];
                    modelObjective = ObjectiveCalculator.Replay(
                        weightedInputs, primaryCtx, state.Sequence, state.Earliest, groupScratch);
                }
            }
            else if (visitPoiIds.Count == 0)
            {
                modelObjective = 0;
            }
        }

        return new TrapSolveResult(result, modelObjective);
    }

    public static TrapFixture CreateMandatoryGainTrapFixture()
    {
        var c1 = OptionalRouteOptimizationScenarios.CreateCandidate(101, "C1", visitDurationMinutes: 30, cost: 0, effectiveDesirabilityScore: 0.35m);
        var c2 = OptionalRouteOptimizationScenarios.CreateCandidate(102, "C2", visitDurationMinutes: 30, cost: 0, effectiveDesirabilityScore: 0.30m);
        var m = OptionalRouteOptimizationScenarios.CreateCandidate(103, "M", visitDurationMinutes: 30, cost: 0, effectiveDesirabilityScore: 0.90m);

        var candidates = new[] { c1, c2, m };
        var indices = new Dictionary<long, int> { [101] = 1, [102] = 2, [103] = 3 };
        var input = OptionalRouteOptimizationScenarios.CreateInput(
            availableMinutes: 300,
            candidates,
            mandatoryPoiIds: [103],
            restPreference: RestPreference.None);

        var matrix = RouteDurationMatrix.Create(new int[,]
        {
            { 0,   1,   5, 500,  10 },
            { 1,   0, 500,  20,   5 },
            { 5, 500,   0,   2,   5 },
            { 500, 20,  2,   0,   5 },
            { 10,  5,   5,   5,   0 },
        });

        var baseByPoiId = new Dictionary<long, double>
        {
            [101] = 0.35,
            [102] = 0.30,
            [103] = 0.90,
        };

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
            ConfigVersion: "mandatory-gain-trap-e2e");

        var cspOptions = new CspOptions
        {
            Mode = ObjectiveMode.WeightedUtility,
            MaxStops = 2
        };

        return new TrapFixture(input, matrix, candidates, indices, new WeightedRequestInputs(baseByPoiId, personalParams, new ConstantTimeFitFactory()), cspOptions);
    }

    public static TrapFixture CreateSlotGreedyTrapFixture()
    {
        var subFirst = OptionalRouteOptimizationScenarios.CreateCandidate(101, "SubFirst", visitDurationMinutes: 30, cost: 0, effectiveDesirabilityScore: 0.60m) with { CategoryName = "Sub" };
        var optFirst = OptionalRouteOptimizationScenarios.CreateCandidate(102, "OptFirst", visitDurationMinutes: 30, cost: 0, effectiveDesirabilityScore: 0.10m) with { CategoryName = "Opt" };
        var quickLow = OptionalRouteOptimizationScenarios.CreateCandidate(103, "QuickLow", visitDurationMinutes: 5, cost: 0, effectiveDesirabilityScore: 0.35m) with { CategoryName = "Quick" };
        var longHigh = OptionalRouteOptimizationScenarios.CreateCandidate(104, "LongHigh", visitDurationMinutes: 60, cost: 0, effectiveDesirabilityScore: 0.70m) with { CategoryName = "Long" };

        var candidates = new[] { subFirst, optFirst, quickLow, longHigh };
        var indices = new Dictionary<long, int> { [101] = 1, [102] = 2, [103] = 3, [104] = 4 };
        var input = OptionalRouteOptimizationScenarios.CreateInput(
            availableMinutes: 300,
            candidates,
            mandatoryPoiIds: [],
            restPreference: RestPreference.None);

        var matrix = RouteDurationMatrix.Create(new int[,]
        {
            { 0,   1,   5, 500, 500,  10 }, // 0: Start
            { 1,   0, 500, 500, 500,   5 }, // 1: SubFirst -> only End
            { 5, 500,   0,   1,   5,  10 }, // 2: OptFirst -> QuickLow(1) or LongHigh(5)
            { 500, 500, 1,   0, 500,   1 }, // 3: QuickLow -> End
            { 500, 500, 5, 500,   0,   5 }, // 4: LongHigh -> End
            { 10,  5,  10,   1,   5,   0 }, // 5: End
        });

        var baseByPoiId = new Dictionary<long, double>
        {
            [101] = 0.60,
            [102] = 0.10,
            [103] = 0.35,
            [104] = 0.70,
        };

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
            ConfigVersion: "slot-greedy-trap-e2e");

        var cspOptions = new CspOptions
        {
            Mode = ObjectiveMode.WeightedUtility,
            MaxStops = 2
        };

        return new TrapFixture(input, matrix, candidates, indices, new WeightedRequestInputs(baseByPoiId, personalParams, new ConstantTimeFitFactory()), cspOptions);
    }

    public static TrapFixture CreateGammaDensityTrapFixture()
    {
        var subFirst = OptionalRouteOptimizationScenarios.CreateCandidate(101, "SubFirst", visitDurationMinutes: 30, cost: 0, effectiveDesirabilityScore: 0.50m);
        var optFirst = OptionalRouteOptimizationScenarios.CreateCandidate(102, "OptFirst", visitDurationMinutes: 30, cost: 0, effectiveDesirabilityScore: 0.10m);
        var cult = OptionalRouteOptimizationScenarios.CreateCandidate(103, "Cult", visitDurationMinutes: 30, cost: 0, effectiveDesirabilityScore: 0.70m) with
        {
            CategoryName = PoiCategoryNames.Culture
        };

        var candidates = new[] { subFirst, optFirst, cult };
        var indices = new Dictionary<long, int> { [101] = 1, [102] = 2, [103] = 3 };
        var input = OptionalRouteOptimizationScenarios.CreateInput(
            availableMinutes: 300,
            candidates,
            mandatoryPoiIds: [],
            restPreference: RestPreference.None);

        var matrix = RouteDurationMatrix.Create(new int[,]
        {
            { 0,   1,   5, 500,  10 }, // 0: Start
            { 1,   0, 500, 500,   1 }, // 1: SubFirst -> only End
            { 5, 500,   0,   2,  10 }, // 2: OptFirst -> Cult(2)
            { 500, 500, 2,   0,   2 }, // 3: Cult -> End(2)
            { 10,  1,  10,   2,   0 }, // 4: End
        });

        var baseByPoiId = new Dictionary<long, double>
        {
            [101] = 0.50,
            [102] = 0.10,
            [103] = 0.70,
        };

        var personalParams = new PersonalParams(
            LambdaPerMin: 0.015,
            Mu: 0.005,
            MaxStopsPerDay: 5,
            MaxDailyEffort: 300,
            RestAfterMin: 120,
            MaxConsecutiveOutdoor: 3,
            SunriseFactor: 0.6,
            GammaDefault: 0.6,
            GammaLoved: 0.50,
            LovedCategories: new HashSet<string> { PoiCategoryNames.Culture },
            RhoOutdoor: 0.25,
            RhoRest: 0.20,
            RhoStops: 0.30,
            RhoEffortPerHour: 0.05,
            ConfigVersion: "gamma-trap-e2e");

        var cspOptions = new CspOptions
        {
            Mode = ObjectiveMode.WeightedUtility,
            MaxStops = 2
        };

        return new TrapFixture(input, matrix, candidates, indices, new WeightedRequestInputs(baseByPoiId, personalParams, new ConstantTimeFitFactory()), cspOptions);
    }

    public static TrapFixture CreateRoundingTrapFixture()
    {
        var sub = OptionalRouteOptimizationScenarios.CreateCandidate(100, "Sub", visitDurationMinutes: 5, cost: 0, effectiveDesirabilityScore: 0.2000m) with { CategoryName = "Sub" };
        var o1 = OptionalRouteOptimizationScenarios.CreateCandidate(101, "O1", visitDurationMinutes: 5, cost: 0, effectiveDesirabilityScore: 0.2000m) with { CategoryName = "O1" };
        var m1 = OptionalRouteOptimizationScenarios.CreateCandidate(102, "M1", visitDurationMinutes: 5, cost: 0, effectiveDesirabilityScore: 0.1000m) with { CategoryName = "M1" };
        var m2 = OptionalRouteOptimizationScenarios.CreateCandidate(103, "M2", visitDurationMinutes: 5, cost: 0, effectiveDesirabilityScore: 0.1000m) with { CategoryName = "M2" };
        var m3 = OptionalRouteOptimizationScenarios.CreateCandidate(104, "M3", visitDurationMinutes: 5, cost: 0, effectiveDesirabilityScore: 0.1000m) with { CategoryName = "M3" };

        var candidates = new[] { sub, o1, m1, m2, m3 };
        var indices = new Dictionary<long, int> { [100] = 1, [101] = 2, [102] = 3, [103] = 4, [104] = 5 };
        var input = OptionalRouteOptimizationScenarios.CreateInput(
            availableMinutes: 300,
            candidates,
            mandatoryPoiIds: [102, 103, 104],
            restPreference: RestPreference.None);

        // Matrix indices:
        // 0: Start
        // 1: Sub (100)
        // 2: O1  (101)
        // 3: M1  (102)
        // 4: M2  (103)
        // 5: M3  (104)
        // 6: End
        var matrix = RouteDurationMatrix.Create(new int[,]
        {
            // 0    1    2    3    4    5    6
            {  0,   5,   5, 500, 500, 500,  10 }, // 0: Start -> Sub(5), O1(5)
            {  5,   0, 500,  10, 500, 500,  10 }, // 1: Sub -> M1(10)
            {  5, 500,   0,   5, 500, 500,  10 }, // 2: O1 -> M1(5)
            { 500, 500, 500,  0,   5, 500,  10 }, // 3: M1 -> M2(5)
            { 500, 500, 500, 500,  0,   5,  10 }, // 4: M2 -> M3(5)
            { 500, 500, 500, 500, 500,  0,   5 }, // 5: M3 -> End(5)
            { 10,  10,  10,  10,  10,   5,   0 }, // 6: End
        });

        var baseByPoiId = new Dictionary<long, double>
        {
            [100] = 0.2000,
            [101] = 0.2000,
            [102] = 0.1000,
            [103] = 0.1000,
            [104] = 0.1000,
        };

        var personalParams = new PersonalParams(
            LambdaPerMin: 0.00001,
            Mu: 0.005,
            MaxStopsPerDay: 10,
            MaxDailyEffort: 300,
            RestAfterMin: 120,
            MaxConsecutiveOutdoor: 10,
            SunriseFactor: 0.6,
            GammaDefault: 0.6,
            GammaLoved: 0.85,
            LovedCategories: new HashSet<string>(),
            RhoOutdoor: 0.25,
            RhoRest: 0.20,
            RhoStops: 0.30,
            RhoEffortPerHour: 0.05,
            ConfigVersion: "rounding-trap-e2e");

        var cspOptions = new CspOptions
        {
            Mode = ObjectiveMode.WeightedUtility,
            MaxStops = 4
        };

        return new TrapFixture(input, matrix, candidates, indices, new WeightedRequestInputs(baseByPoiId, personalParams, new ConstantTimeFitFactory()), cspOptions);
    }

    [Fact]
    public void MandatoryGainTrap_EndToEnd_FaultyBound_Prunes_Optimal()
    {
        var fixture = CreateMandatoryGainTrapFixture();
        var brute = Solve(fixture, BoundVariant.Correct, disableBound: true);
        var correct = Solve(fixture, BoundVariant.Correct);
        var faulty = Solve(fixture, BoundVariant.OmitMandatoryGain);

        Assert.NotNull(brute.BestModelObjective);
        Assert.Equal(brute.BestModelObjective, correct.BestModelObjective);
        Assert.True(faulty.BestModelObjective < brute.BestModelObjective,
            $"Expected faulty ({faulty.BestModelObjective}) < brute ({brute.BestModelObjective})");
    }

    [Fact]
    public void SlotGreedyTrap_EndToEnd_FaultyBound_Prunes_Optimal()
    {
        var fixture = CreateSlotGreedyTrapFixture();
        var brute = Solve(fixture, BoundVariant.Correct, disableBound: true);
        var correct = Solve(fixture, BoundVariant.Correct);
        var faulty = Solve(fixture, BoundVariant.GreedySlotsOnly);

        Assert.NotNull(brute.BestModelObjective);
        Assert.Equal(brute.BestModelObjective, correct.BestModelObjective);
        Assert.True(faulty.BestModelObjective < brute.BestModelObjective,
            $"Expected faulty ({faulty.BestModelObjective}) < brute ({brute.BestModelObjective})");
    }

    [Fact]
    public void GammaDensityTrap_EndToEnd_FaultyBound_Prunes_Optimal()
    {
        var fixture = CreateGammaDensityTrapFixture();
        var brute = Solve(fixture, BoundVariant.Correct, disableBound: true);
        var correct = Solve(fixture, BoundVariant.Correct);
        var faulty = Solve(fixture, BoundVariant.ApplyGamma);

        Assert.NotNull(brute.BestModelObjective);
        Assert.Equal(brute.BestModelObjective, correct.BestModelObjective);
        Assert.True(faulty.BestModelObjective < brute.BestModelObjective,
            $"Expected faulty ({faulty.BestModelObjective}) < brute ({brute.BestModelObjective})");
    }

    [Fact]
    public void RoundingTrap_EndToEnd_FaultyBound_Prunes_Optimal()
    {
        var fixture = CreateRoundingTrapFixture();
        var brute = Solve(fixture, BoundVariant.Correct, disableBound: true);
        var correct = Solve(fixture, BoundVariant.Correct);
        var faulty = Solve(fixture, BoundVariant.NoRoundingSlack);

        Assert.NotNull(brute.BestModelObjective);
        Assert.Equal(brute.BestModelObjective, correct.BestModelObjective);
        Assert.True(faulty.BestModelObjective < brute.BestModelObjective,
            $"Expected faulty ({faulty.BestModelObjective}) < brute ({brute.BestModelObjective})");
    }
}
