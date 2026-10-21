using FluentAssertions;

using TripMate.Application.Features.Scheduling.Common;
using TripMate.Application.Features.Scheduling.Csp;
using TripMate.Application.Features.Scheduling.Csp.Objectives;
using TripMate.Application.Features.Scheduling.Personalization;
using TripMate.Application.Features.Scheduling.Routing;
using TripMate.Application.UnitTests.Features.Scheduling.Fixtures;
using TripMate.Application.UnitTests.Features.Scheduling.Golden;
using TripMate.Domain.Enums;

using Xunit;

namespace TripMate.Application.UnitTests.Features.Scheduling.Objectives;

public class ObjectiveKernelFactoryTests
{
    private static readonly SchedulingGenerationOptions Options = new();

    private static (RoutingContext Context, CspProductRule Rule, WeightedObjectiveInputs Inputs) CreateTestContext()
    {
        var allScenarios = GoldenScenarioFactory.CreateAllScenarios();
        var scenario = allScenarios.First(s => s.Id == "grid-c10-m0-none-s1");
        var ctx = CspItinerarySolver.CreatePrimaryContext(
            scenario.Input, scenario.Matrix, scenario.Candidates, scenario.Indices, Options);
        var rule = CspProductRule.Create(ctx, scenario.Candidates, scenario.Indices, scenario.Matrix);

        var n = ctx.Nodes.Count;
        var baseScores = new double[n];
        for (var i = 0; i < n; i++)
        {
            baseScores[i] = ((i * 37 + 13) % 100) / 100.0;
        }

        var gamma = Enumerable.Range(0, n)
            .Select(_ => Enumerable.Range(0, 20)
                .Select(k => (long)Math.Round(Math.Pow(0.6, k) * WeightedObjectiveInputs.Scale))
                .ToArray())
            .ToArray();

        var p = new PersonalParams(
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
            ConfigVersion: "test");

        var inputs = new WeightedObjectiveInputs
        {
            Base = baseScores,
            DiversityGroupIndex = new int[n],
            DiversityGroupCount = 1,
            GammaScaledByNode = gamma,
            IsExposedOutdoor = new bool[n],
            IsFood = new bool[n],
            IsRestStop = new bool[n],
            Effort = new int[n],
            TimeFit = new ConstantTimeFit(),
            P = p
        };

        return (ctx, rule, inputs);
    }

    [Fact]
    public void Create_LexicographicMode_Returns_LexicographicKernel()
    {
        var (ctx, rule, _) = CreateTestContext();
        var options = new CspOptions { Mode = ObjectiveMode.Lexicographic };

        var kernel = ObjectiveKernelFactory.Create(rule, options);

        kernel.Should().BeOfType<LexicographicKernel>();
    }

    [Fact]
    public void Create_WeightedUtilityMode_Without_Inputs_Throws_InvalidOperationException()
    {
        var (ctx, rule, _) = CreateTestContext();
        var options = new CspOptions { Mode = ObjectiveMode.WeightedUtility };

        var act = () => ObjectiveKernelFactory.Create(rule, options);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*WeightedObjectiveInputs*");
    }

    [Fact]
    public void Create_With_RoutingContext_In_WeightedUtilityMode_Throws_When_WeightedRequestInputs_Is_Null()
    {
        var (ctx, rule, _) = CreateTestContext();
        var options = new CspOptions { Mode = ObjectiveMode.WeightedUtility };

        var act = () => ObjectiveKernelFactory.Create(ctx, rule, options, weighted: null);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*WeightedRequestInputs*");
    }

    [Fact]
    public void Solver_Throws_InvalidOperationException_When_Mode_Is_WeightedUtility_And_WeightedRequestInputs_Is_Null()
    {
        var allScenarios = GoldenScenarioFactory.CreateAllScenarios();
        var scenario = allScenarios.First(s => s.Id == "grid-c10-m0-none-s1");
        var evaluator = new ItineraryScheduleEvaluator(Options);
        var cspOptions = new CspOptions { Mode = ObjectiveMode.WeightedUtility };
        var solver = new CspItinerarySolver(evaluator, Options, cspOptions);

        var act = () => solver.Solve(
            scenario.Input, scenario.Matrix, scenario.Candidates, scenario.Indices,
            scenario.InitialIncumbent, weighted: null, CancellationToken.None);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*WeightedRequestInputs must be provided*");
    }

    [Fact]
    public void Create_WeightedUtilityMode_With_Inputs_Returns_WeightedUtilityKernel()
    {
        var (ctx, rule, inputs) = CreateTestContext();
        var options = new CspOptions { Mode = ObjectiveMode.WeightedUtility };

        var kernel = ObjectiveKernelFactory.Create(rule, options, inputs);

        kernel.Should().BeOfType<WeightedUtilityKernel>();
    }

    [Fact]
    public void BuildBaseScores_When_Candidate_Is_Filtered_Out_Accurately_Matches_Surviving_Node_PoiIds()
    {
        var c1 = OptionalRouteOptimizationScenarios.CreateCandidate(101, "C1", 30, 0, 0.8m) with { CategoryName = "Culture" };
        var c2 = OptionalRouteOptimizationScenarios.CreateCandidate(102, "C2", 30, 0, 0.7m) with { CategoryName = "restaurant", HasShelter = true };
        var c3 = OptionalRouteOptimizationScenarios.CreateCandidate(103, "C3", 30, 0, 0.9m) with { CategoryName = "Nature" };


        var candidates = new[] { c1, c2, c3 };
        var indices = new Dictionary<long, int> { [101] = 1, [102] = 2, [103] = 3 };
        var input = OptionalRouteOptimizationScenarios.CreateInput(480, candidates, mandatoryPoiIds: [], restPreference: RestPreference.None);
        var matrix = RouteDurationMatrix.Create(new int[5, 5]);

        var ctx = CspItinerarySolver.CreatePrimaryContext(input, matrix, candidates, indices, Options);

        // POI 102 (Food & Drink) bị lọc khỏi ctx.Nodes vì là QualifiedRestCandidate
        ctx.Nodes.Should().HaveCount(2);
        ctx.Nodes[0].Candidate.Id.Should().Be(101);
        ctx.Nodes[1].Candidate.Id.Should().Be(103);

        var baseByPoiId = new Dictionary<long, double>
        {
            [101] = 0.75,
            [102] = 0.40,
            [103] = 0.95,
        };

        var baseScores = ObjectiveKernelFactory.BuildBaseScores(ctx, baseByPoiId);

        baseScores.Should().HaveCount(2);
        baseScores[0].Should().Be(0.75, "Node 0 (POI 101) must receive Base from BaseByPoiId[101]");
        baseScores[1].Should().Be(0.95, "Node 1 (POI 103) must receive Base from BaseByPoiId[103]");
    }


    [Fact]
    public void BuildBaseScores_Throws_InvalidOperationException_When_PoiId_Is_Missing_From_BaseByPoiId()
    {
        var (ctx, _, _) = CreateTestContext();
        var incompleteBase = new Dictionary<long, double>(); // empty

        var act = () => ObjectiveKernelFactory.BuildBaseScores(ctx, incompleteBase);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*missing in BaseByPoiId*");
    }

    [Theory]
    [InlineData(-0.01)]
    [InlineData(1.01)]
    [InlineData(double.NaN)]
    public void WeightedUtilityKernel_Throws_When_Base_Score_Violates_Unit_Interval(double invalidBase)
    {
        var (ctx, rule, inputs) = CreateTestContext();
        inputs.Base[0] = invalidBase;

        var options = new CspOptions { Mode = ObjectiveMode.WeightedUtility };
        var kernel = ObjectiveKernelFactory.Create(rule, options, inputs);

        var buffers = new SearchBuffers(ctx.Nodes.Count + 1, ctx.Nodes.Count);
        var pre = new SearchPrecomputed(
            new int[ctx.Nodes.Count], new int[ctx.Nodes.Count], new int[ctx.Nodes.Count],
            new int[ctx.Nodes.Count], new int[ctx.Nodes.Count], 0, 1, [0], [true]);

        var act = () => kernel.Initialize(ctx, buffers, pre, new CspStatistics());

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*Invariant violation*Base*outside [0, 1]*");
    }

    [Fact]
    public void Solver_Throws_NotSupportedException_When_Polish_Is_Enabled_In_WeightedUtilityMode()
    {
        var allScenarios = GoldenScenarioFactory.CreateAllScenarios();
        var scenario = allScenarios.First(s => s.Id == "grid-c10-m0-none-s1");
        var evaluator = new ItineraryScheduleEvaluator(Options);

        var cspOptions = new CspOptions
        {
            Mode = ObjectiveMode.WeightedUtility,
            PolishWithLocalSearch = true
        };
        var solver = new CspItinerarySolver(evaluator, Options, cspOptions);

        var act = () => solver.Solve(
            scenario.Input, scenario.Matrix, scenario.Candidates, scenario.Indices,
            scenario.InitialIncumbent, CancellationToken.None);

        act.Should().Throw<NotSupportedException>()
            .WithMessage("*PolishWithLocalSearch*");
    }

    [Fact]
    public void BuildRankRuleFromBase_Sorts_By_Base_Descending_And_Ties_With_CandidateId()
    {
        var (ctx, _, _) = CreateTestContext();
        var n = ctx.Nodes.Count;

        var baseScores = new double[n];
        for (var i = 0; i < n; i++)
        {
            baseScores[i] = 0.5;
        }

        if (ctx.OptionalNodes.Count >= 2)
        {
            var opt1 = ctx.OptionalNodes[0];
            var opt2 = ctx.OptionalNodes[1];
            baseScores[opt1] = 0.9;
            baseScores[opt2] = 0.9;

            var rule = CspProductRule.BuildRankRuleFromBase(ctx, baseScores);

            var smallerIdNode = ctx.Nodes[opt1].Candidate.Id < ctx.Nodes[opt2].Candidate.Id ? opt1 : opt2;
            var largerIdNode = smallerIdNode == opt1 ? opt2 : opt1;

            rule.RankOf(smallerIdNode).Should().BeLessThan(rule.RankOf(largerIdNode));
            rule.RankOf(smallerIdNode).Should().Be(0, "Highest base score must have rank 0");
            rule.RankOf(largerIdNode).Should().Be(1, "Second highest / tie-broken by ID must have rank 1");
        }
    }


    [Fact]
    public void Solver_Executes_Successfully_In_WeightedUtility_Mode()
    {
        var allScenarios = GoldenScenarioFactory.CreateAllScenarios();
        var scenario = allScenarios.First(s => s.Id == "grid-c10-m0-none-s1");
        var evaluator = new ItineraryScheduleEvaluator(Options);

        var baseByPoiId = scenario.Candidates.ToDictionary(c => c.Id, c => 0.5);
        var request = new WeightedRequestInputs(
            baseByPoiId,
            new PersonalParams(0.015, 0.005, 5, 300, 120, 3, 0.6, 0.6, 0.85, new HashSet<string>(), 0.25, 0.20, 0.30, 0.05, "test"),
            new ConstantTimeFitFactory());

        var cspOptions = new CspOptions
        {
            Mode = ObjectiveMode.WeightedUtility,
            TimeLimitMilliseconds = 10_000
        };

        var solver = new CspItinerarySolver(evaluator, Options, cspOptions);
        var result = solver.Solve(
            scenario.Input, scenario.Matrix, scenario.Candidates, scenario.Indices,
            scenario.InitialIncumbent, weighted: request, cancellationToken: CancellationToken.None);

        result.Schedule.Should().NotBeNull();
        result.EliteVisitIds.Should().NotBeEmpty();
    }

    [Fact]
    public void BuildWeightedInputs_WithCulturePersona_AppliesGammaLovedToCultureAndMuseumCategories()
    {
        var openingHours = new[] { new GenerationOpeningHours(2, new TimeOnly(7, 0), new TimeOnly(21, 0)) };
        var c1 = new GenerationCandidate(
            1L, "Museum POI", new RoutePoint(16.04m, 108.22m), 90, 50_000m, openingHours,
            CategoryName: "Museum");
        var c2 = new GenerationCandidate(
            2L, "Culture POI", new RoutePoint(16.05m, 108.23m), 45, 0m, openingHours,
            CategoryName: "Culture");
        var c3 = new GenerationCandidate(
            3L, "Theme Park POI", new RoutePoint(16.06m, 108.24m), 120, 100_000m, openingHours,
            CategoryName: "Theme park");

        var candidates = new[] { c1, c2, c3 };
        var indices = new Dictionary<long, int> { [1L] = 1, [2L] = 2, [3L] = 3 };
        var input = new GenerationInput(
            DateTimeOffset.UtcNow,
            TimeZoneInfo.Utc,
            new RoutePoint(16.0m, 108.2m),
            new RoutePoint(16.1m, 108.3m),
            480,
            TransportMode.Motorbike,
            RestPreference.None,
            null,
            candidates,
            []);

        var matrix = RouteDurationMatrix.Create(new int[,]
        {
            { 0, 10, 15, 20, 25 },
            { 10, 0, 10, 15, 20 },
            { 15, 10, 0, 10, 15 },
            { 20, 15, 10, 0, 10 },
            { 25, 20, 15, 10, 0 }
        });

        var ctx = CspItinerarySolver.CreatePrimaryContext(input, matrix, candidates, indices, Options);

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
            LovedCategories: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Culture", "Museum" },
            RhoOutdoor: 0.25,
            RhoRest: 0.20,
            RhoStops: 0.30,
            RhoEffortPerHour: 0.05,
            ConfigVersion: "test-culture-gamma");

        var baseScores = new Dictionary<long, double> { [1L] = 0.8, [2L] = 0.7, [3L] = 0.5 };
        var weightedRequest = new WeightedRequestInputs(baseScores, personalParams, new ConstantTimeFitFactory());

        var weightedInputs = ObjectiveKernelFactory.BuildWeightedInputs(ctx, weightedRequest);

        // Find category indices for nodes
        var museumNode = 0; // c1
        var cultureNode = 1; // c2
        var themeParkNode = 2; // c3

        // Culture and Museum share the same diversity discount group
        weightedInputs.DiversityGroupIndex[museumNode].Should().Be(
            weightedInputs.DiversityGroupIndex[cultureNode],
            "Culture and Museum must belong to the same diversity group");
        weightedInputs.DiversityGroupIndex[themeParkNode].Should().NotBe(
            weightedInputs.DiversityGroupIndex[museumNode],
            "Theme park must be in an independent diversity group");

        // Culture and Museum must receive GammaLoved = 0.85
        var expectedLovedK1 = (long)Math.Round(0.85 * WeightedObjectiveInputs.Scale);
        var expectedDefaultK1 = (long)Math.Round(0.60 * WeightedObjectiveInputs.Scale);

        weightedInputs.GammaScaledByNode[museumNode][1].Should().Be(expectedLovedK1, "Museum POI must receive GammaLoved");
        weightedInputs.GammaScaledByNode[cultureNode][1].Should().Be(expectedLovedK1, "Culture POI must receive GammaLoved");

        // Theme Park is not in LovedCategories -> receives GammaDefault = 0.60
        weightedInputs.GammaScaledByNode[themeParkNode][1].Should().Be(expectedDefaultK1, "Theme park POI must receive GammaDefault");
    }

    [Fact]
    public void BuildWeightedInputs_WithGeneralCategory_ExemptsFromGammaDiscount()
    {
        var openingHours = new[] { new GenerationOpeningHours(2, new TimeOnly(7, 0), new TimeOnly(21, 0)) };
        var candidates = new List<GenerationCandidate>();
        var indices = new Dictionary<long, int>();
        var baseScores = new Dictionary<long, double>();

        for (var i = 1; i <= 4; i++)
        {
            candidates.Add(new GenerationCandidate(
                (long)i, $"General POI {i}", new RoutePoint(16.0m + i * 0.01m, 108.2m), 30, 0m, openingHours,
                CategoryName: null)); // null -> "General"
            indices[(long)i] = i;
            baseScores[(long)i] = 0.5;
        }

        var input = new GenerationInput(
            DateTimeOffset.UtcNow,
            TimeZoneInfo.Utc,
            new RoutePoint(16.0m, 108.2m),
            new RoutePoint(16.1m, 108.3m),
            480,
            TransportMode.Motorbike,
            RestPreference.None,
            null,
            candidates,
            []);

        var matrix = RouteDurationMatrix.Create(new int[,]
        {
            { 0, 5, 10, 15, 20, 25 },
            { 5, 0, 5, 10, 15, 20 },
            { 10, 5, 0, 5, 10, 15 },
            { 15, 10, 5, 0, 5, 10 },
            { 20, 15, 10, 5, 0, 5 },
            { 25, 20, 15, 10, 5, 0 }
        });

        var ctx = CspItinerarySolver.CreatePrimaryContext(input, matrix, candidates, indices, Options);
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
            ConfigVersion: "test-general-gamma");

        var weightedRequest = new WeightedRequestInputs(baseScores, personalParams, new ConstantTimeFitFactory());
        var weightedInputs = ObjectiveKernelFactory.BuildWeightedInputs(ctx, weightedRequest);

        for (var i = 0; i < 4; i++)
        {
            for (var k = 0; k < weightedInputs.GammaScaledByNode[i].Length; k++)
            {
                weightedInputs.GammaScaledByNode[i][k].Should().Be(
                    WeightedObjectiveInputs.Scale,
                    $"General category at k={k} must have scale factor 1.0 (no discount)");
            }
        }

        // Test with ObjectiveCalculator.Replay: 4 General POIs in sequence
        var groupScratch = new int[weightedInputs.DiversityGroupCount];
        var seq = new[] { 0, 1, 2, 3 };
        var earliest = new[] { 10, 50, 90, 130 };
        var totalJ = ObjectiveCalculator.Replay(weightedInputs, ctx, seq, earliest, groupScratch);

        // Every node contributes base (0.5 * 1,000,000 = 500,000) with gamma=1.0 minus travel cost
        // Total gain should be 4 * 500,000 = 2,000,000
        var totalGain = 4 * (long)(0.5 * WeightedObjectiveInputs.Scale);
        totalJ.Should().BeLessThan(totalGain); // travel and rest penalties subtracted, but gross gain is 2,000,000
    }

    [Fact]
    public void BuildWeightedInputs_WithRealCategory_AppliesGammaDiscountAsUsual()
    {
        var openingHours = new[] { new GenerationOpeningHours(2, new TimeOnly(7, 0), new TimeOnly(21, 0)) };
        var candidates = new List<GenerationCandidate>();
        var indices = new Dictionary<long, int>();
        var baseScores = new Dictionary<long, double>();

        for (var i = 1; i <= 4; i++)
        {
            candidates.Add(new GenerationCandidate(
                (long)i, $"Museum POI {i}", new RoutePoint(16.0m + i * 0.01m, 108.2m), 30, 0m, openingHours,
                CategoryName: "Museum"));
            indices[(long)i] = i;
            baseScores[(long)i] = 0.5;
        }

        var input = new GenerationInput(
            DateTimeOffset.UtcNow,
            TimeZoneInfo.Utc,
            new RoutePoint(16.0m, 108.2m),
            new RoutePoint(16.1m, 108.3m),
            480,
            TransportMode.Motorbike,
            RestPreference.None,
            null,
            candidates,
            []);

        var matrix = RouteDurationMatrix.Create(new int[,]
        {
            { 0, 5, 10, 15, 20, 25 },
            { 5, 0, 5, 10, 15, 20 },
            { 10, 5, 0, 5, 10, 15 },
            { 15, 10, 5, 0, 5, 10 },
            { 20, 15, 10, 5, 0, 5 },
            { 25, 20, 15, 10, 5, 0 }
        });

        var ctx = CspItinerarySolver.CreatePrimaryContext(input, matrix, candidates, indices, Options);
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
            LovedCategories: new HashSet<string>(), // Not loved -> GammaDefault = 0.6
            RhoOutdoor: 0.25,
            RhoRest: 0.20,
            RhoStops: 0.30,
            RhoEffortPerHour: 0.05,
            ConfigVersion: "test-real-gamma");

        var weightedRequest = new WeightedRequestInputs(baseScores, personalParams, new ConstantTimeFitFactory());
        var weightedInputs = ObjectiveKernelFactory.BuildWeightedInputs(ctx, weightedRequest);

        weightedInputs.GammaScaledByNode[0][0].Should().Be((long)(1.0 * WeightedObjectiveInputs.Scale));
        weightedInputs.GammaScaledByNode[0][1].Should().Be((long)Math.Round(0.6 * WeightedObjectiveInputs.Scale));
        weightedInputs.GammaScaledByNode[0][2].Should().Be((long)Math.Round(0.36 * WeightedObjectiveInputs.Scale));
        weightedInputs.GammaScaledByNode[0][3].Should().Be((long)Math.Round(0.216 * WeightedObjectiveInputs.Scale));
    }
}