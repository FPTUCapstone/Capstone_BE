using FluentAssertions;
using TripMate.Application.Features.Scheduling.Common;
using TripMate.Application.Features.Scheduling.Csp;
using TripMate.Application.Features.Scheduling.Csp.Objectives;
using TripMate.Application.Features.Scheduling.Personalization;
using TripMate.Application.Features.Scheduling.Routing;
using TripMate.Domain.Constants;
using TripMate.Domain.Enums;
using Xunit;

namespace TripMate.Application.UnitTests.Features.Scheduling.Objectives;

public class CategoryGroupingDiversityTests
{
    private static readonly SchedulingGenerationOptions Options = new()
    {
        TransitionBufferMinutes = 0,
        FinalReturnBufferMinutes = 0
    };

    private static (RoutingContext Context, WeightedObjectiveInputs Inputs) CreateScenario(
        (string Name, string? CategoryName)[] poiDefs,
        IReadOnlySet<string>? lovedCategories = null,
        double gammaDefault = 0.6,
        double gammaLoved = 0.85)
    {
        var openingHours = Enumerable.Range(0, 7)
            .Select(d => new GenerationOpeningHours((byte)d, new TimeOnly(0, 0), new TimeOnly(23, 59)))
            .ToArray();
        var candidates = new List<GenerationCandidate>();
        var indices = new Dictionary<long, int>();
        var baseScores = new Dictionary<long, double>();

        for (var i = 0; i < poiDefs.Length; i++)
        {
            var id = (long)(i + 1);
            candidates.Add(new GenerationCandidate(
                id,
                poiDefs[i].Name,
                new RoutePoint(16.0m + i * 0.01m, 108.2m),
                30,
                0m,
                openingHours,
                CategoryName: poiDefs[i].CategoryName,
                EffectiveDesirabilityScore: 1.0m,
                TripMateBaseScore: 1.0m,
                IndoorOutdoor: IndoorOutdoorType.Indoor,
                HasShelter: false));
            indices[id] = i + 1;
            baseScores[id] = 1.0; // Uniform 1.0 base score for clear assertions
        }

        var startAtUtc = new DateTimeOffset(2026, 10, 20, 1, 0, 0, TimeSpan.Zero);
        var mandatoryIds = candidates.Select(c => c.Id).ToArray();
        var input = new GenerationInput(
            startAtUtc,
            TimeZoneInfo.Utc,
            new RoutePoint(16.0m, 108.2m),
            new RoutePoint(16.0m, 108.2m),
            480,
            TransportMode.Motorbike,
            RestPreference.None,
            null,
            candidates,
            mandatoryIds);

        // Zero travel matrix so objective gain is pure sum of visit gains without travel penalty
        var pointCount = poiDefs.Length + 2;
        var matrix = RouteDurationMatrix.Create(new int[pointCount, pointCount]);

        var ctx = CspItinerarySolver.CreatePrimaryContext(input, matrix, candidates, indices, Options);
        var personalParams = new PersonalParams(
            LambdaPerMin: 0.0, // Zero travel penalty
            Mu: 0.0,           // Zero idle penalty
            MaxStopsPerDay: 10,
            MaxDailyEffort: 600,
            RestAfterMin: 600,
            MaxConsecutiveOutdoor: 10,
            SunriseFactor: 0.6,
            GammaDefault: gammaDefault,
            GammaLoved: gammaLoved,
            LovedCategories: lovedCategories ?? new HashSet<string>(),
            RhoOutdoor: 0.0,
            RhoRest: 0.0,
            RhoStops: 0.0,
            RhoEffortPerHour: 0.0,
            ConfigVersion: "test-category-grouping");

        var weightedRequest = new WeightedRequestInputs(baseScores, personalParams, new ConstantTimeFitFactory());
        var weightedInputs = ObjectiveKernelFactory.BuildWeightedInputs(ctx, weightedRequest);

        return (ctx, weightedInputs);
    }

    [Fact]
    public void Culture_Then_Museum_Shares_DiversityGroup_Counter_K0_K1()
    {
        // Arrange: C1 (Culture) and C2 (Museum). Neither is loved.
        // Group Cultural: C1 gets k=0 (gamma^0 = 1.0), C2 gets k=1 (gamma^1 = 0.60).
        var (ctx, inputs) = CreateScenario([
            ("Bridge", PoiCategoryNames.Culture),
            ("Museum", PoiCategoryNames.Museum)
        ]);

        var groupScratch = new int[inputs.DiversityGroupCount];
        var seq = new[] { 0, 1 };
        var earliest = new[] { 0, 30 };

        // Act
        var totalJ = ObjectiveCalculator.Replay(inputs, ctx, seq, earliest, groupScratch);

        // Expected gross gain:
        // C1: Base (1.0) * gamma^0 (1.0) = 10,000
        // C2: Base (1.0) * gamma^1 (0.60) = 6,000
        // Total expected = 16,000
        // (If independent counters, C2 gets k=0 -> total would be 20,000)
        var expectedJ = 10_000L + 6_000L;
        totalJ.Should().Be(expectedJ);
    }

    [Fact]
    public void Museum_Then_Culture_Factor_Per_Node_Counter_Per_Group()
    {
        // Arrange: C1 (Museum) not loved, C2 (Culture) LOVED.
        // Group Cultural:
        // C1 (Museum): k=0, factor=GammaDefault (0.6) -> gamma = 0.60^0 = 1.0 -> gain = 10,000
        // C2 (Culture): k=1 (shared counter), factor=GammaLoved (0.85) -> gamma = 0.85^1 = 0.85 -> gain = 8,500
        // Total expected = 18,500
        var loved = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { PoiCategoryNames.Culture };
        var (ctx, inputs) = CreateScenario([
            ("Museum", PoiCategoryNames.Museum),
            ("Temple", PoiCategoryNames.Culture)
        ], lovedCategories: loved);

        var groupScratch = new int[inputs.DiversityGroupCount];
        var seq = new[] { 0, 1 };
        var earliest = new[] { 0, 30 };

        // Act
        var totalJ = ObjectiveCalculator.Replay(inputs, ctx, seq, earliest, groupScratch);

        var expectedJ = 10_000L + 8_500L;
        totalJ.Should().Be(expectedJ);
    }

    [Fact]
    public void Nature_And_NaturalAttraction_Share_DiversityGroup_Counter()
    {
        // Arrange: C1 (Nature) and C2 (Natural attraction). Neither loved.
        // Group Nature: C1 gets k=0 (1.0), C2 gets k=1 (0.60). Total = 16,000.
        var (ctx, inputs) = CreateScenario([
            ("Park", PoiCategoryNames.Nature),
            ("Mountain", PoiCategoryNames.NaturalAttraction)
        ]);

        var groupScratch = new int[inputs.DiversityGroupCount];
        var seq = new[] { 0, 1 };
        var earliest = new[] { 0, 30 };

        // Act
        var totalJ = ObjectiveCalculator.Replay(inputs, ctx, seq, earliest, groupScratch);

        var expectedJ = 10_000L + 6_000L;
        totalJ.Should().Be(expectedJ);
    }

    [Fact]
    public void ThemePark_And_WaterPark_Do_NOT_Share_DiversityGroup_Counter()
    {
        // Arrange: C1 (Theme park) and C2 (Water park).
        // Under Plan B, Theme park and Water park are SEPARATE groups.
        // Both start at k=0 -> each gets gamma^0 = 1.0 -> Total = 20,000.
        var (ctx, inputs) = CreateScenario([
            ("Amusement Park", PoiCategoryNames.ThemePark),
            ("Aqua Park", PoiCategoryNames.WaterPark)
        ]);

        var groupScratch = new int[inputs.DiversityGroupCount];
        var seq = new[] { 0, 1 };
        var earliest = new[] { 0, 30 };

        // Act
        var totalJ = ObjectiveCalculator.Replay(inputs, ctx, seq, earliest, groupScratch);

        var expectedJ = 10_000L + 10_000L;
        totalJ.Should().Be(expectedJ);
    }

    [Fact]
    public void General_Category_Null_Or_Blank_Exempt_From_Diversity_Discount()
    {
        // Arrange: 3 nodes with null, empty, and whitespace category.
        // All resolve to General -> gamma = 1.0 for all k -> Total = 30,000.
        var (ctx, inputs) = CreateScenario([
            ("Generic POI 1", null),
            ("Generic POI 2", ""),
            ("Generic POI 3", "   ")
        ]);

        var groupScratch = new int[inputs.DiversityGroupCount];
        var seq = new[] { 0, 1, 2 };
        var earliest = new[] { 0, 30, 60 };

        // Act
        var totalJ = ObjectiveCalculator.Replay(inputs, ctx, seq, earliest, groupScratch);

        var expectedJ = 30_000L;
        totalJ.Should().Be(expectedJ);
    }

    [Fact]
    public void Two_Different_Unknown_NonEmpty_Categories_Do_NOT_Share_Counter()
    {
        // Arrange: C1 ("Historical Landmark") and C2 ("Traditional Craft Village").
        // Two distinct non-empty unknown categories: each forms its own independent group.
        // Both get k=0, gammaDefault^0 = 1.0 -> Total = 20,000.
        var (ctx, inputs) = CreateScenario([
            ("Landmark", "Historical Landmark"),
            ("Village", "Traditional Craft Village")
        ]);

        var groupScratch = new int[inputs.DiversityGroupCount];
        var seq = new[] { 0, 1 };
        var earliest = new[] { 0, 30 };

        // Act
        var totalJ = ObjectiveCalculator.Replay(inputs, ctx, seq, earliest, groupScratch);

        var expectedJ = 20_000L;
        totalJ.Should().Be(expectedJ);
    }

    [Fact]
    public void Three_Nodes_Same_Group_Apply_Gamma_Powers_0_1_2()
    {
        // Arrange: 3 cultural nodes: C1 (Culture), C2 (Museum), C3 (Culture).
        // Neither loved.
        // k=0: 1.0 -> 10,000
        // k=1: 0.6 -> 6,000
        // k=2: 0.36 -> 3,600
        // Total expected = 19,600
        var (ctx, inputs) = CreateScenario([
            ("Pagoda", PoiCategoryNames.Culture),
            ("History Museum", PoiCategoryNames.Museum),
            ("Ancient Bridge", PoiCategoryNames.Culture)
        ]);

        var groupScratch = new int[inputs.DiversityGroupCount];
        var seq = new[] { 0, 1, 2 };
        var earliest = new[] { 0, 30, 60 };

        // Act
        var totalJ = ObjectiveCalculator.Replay(inputs, ctx, seq, earliest, groupScratch);

        var expectedJ = 10_000L + 6_000L + 3_600L;
        totalJ.Should().Be(expectedJ);
    }

    [Fact]
    public void Forty_CulturalNodes_UseFullLengthGammaTable_WithoutIndexOutOfRange()
    {
        var poiDefinitions = Enumerable.Range(0, 40)
            .Select(i => (Name: $"Cultural POI {i + 1}", CategoryName: (string?)(i % 2 == 0 ? PoiCategoryNames.Culture : PoiCategoryNames.Museum)))
            .ToArray();
        var (ctx, inputs) = CreateScenario(poiDefinitions);
        var sequence = Enumerable.Range(0, 40).ToArray();
        var starts = sequence.Select(i => i * 30).ToArray();
        var groupScratch = new int[inputs.DiversityGroupCount];

        var action = () => ObjectiveCalculator.Replay(inputs, ctx, sequence, starts, groupScratch);

        action.Should().NotThrow<IndexOutOfRangeException>();
        inputs.GammaScaledByNode.Should().HaveCount(40);
        inputs.GammaScaledByNode.Should().OnlyContain(row => row.Length == 40);
        action().Should().Be(25_001L);
    }

    [Fact]
    public void Kernel_Incremental_Push_Pop_Matches_Replay_BitForBit_With_Category_Grouping()
    {
        var loved = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { PoiCategoryNames.Culture };
        var (ctx, inputs) = CreateScenario([
            ("Bridge", PoiCategoryNames.Culture),
            ("Museum", PoiCategoryNames.Museum),
            ("Park", PoiCategoryNames.Nature),
            ("Mountain", PoiCategoryNames.NaturalAttraction)
        ], lovedCategories: loved);

        var n = ctx.Nodes.Count;
        var buffers = new SearchBuffers(10, n);
        var initialDomain = Enumerable.Range(0, n).ToArray();
        var inInitialDomain = Enumerable.Repeat(true, n).ToArray();
        var pre = new SearchPrecomputed(
            new int[n], new int[n], new int[n], new int[n], new int[n],
            0, 100, initialDomain, inInitialDomain);

        var kernel = new WeightedUtilityKernel(inputs, new CspOptions { Mode = ObjectiveMode.WeightedUtility });
        kernel.Initialize(ctx, buffers, pre, new CspStatistics());

        var groupScratch = new int[inputs.DiversityGroupCount];

        // 1. Push node 0 (Culture)
        buffers.Sequence[0] = 0;
        buffers.Departure[0] = 30;
        buffers.Contains[0] = true;
        kernel.OnPush(0, 0, 0, ctx.StartMatrixIndex);

        var key0 = kernel.PeekEliteKey(1, 30, ctx.Nodes[0].MatrixIndex, 0, 30);
        var replay0 = ObjectiveCalculator.Replay(inputs, ctx, [0], [0], groupScratch);
        ((long)Math.Round(key0.Utility * WeightedObjectiveInputs.Scale)).Should().Be(replay0);

        // 2. Push node 1 (Museum) - shares group with node 0
        buffers.Sequence[1] = 1;
        buffers.Departure[1] = 60;
        buffers.Contains[1] = true;
        kernel.OnPush(1, 1, 30, ctx.Nodes[0].MatrixIndex);

        var key1 = kernel.PeekEliteKey(2, 60, ctx.Nodes[1].MatrixIndex, 0, 60);
        var replay1 = ObjectiveCalculator.Replay(inputs, ctx, [0, 1], [0, 30], groupScratch);
        ((long)Math.Round(key1.Utility * WeightedObjectiveInputs.Scale)).Should().Be(replay1);

        // 3. Pop node 1, push node 2 (Nature)
        buffers.Contains[1] = false;
        kernel.OnPop(1, 1);

        buffers.Sequence[1] = 2;
        buffers.Departure[1] = 60;
        buffers.Contains[2] = true;
        kernel.OnPush(1, 2, 30, ctx.Nodes[0].MatrixIndex);

        var key2 = kernel.PeekEliteKey(2, 60, ctx.Nodes[2].MatrixIndex, 0, 60);
        var replay2 = ObjectiveCalculator.Replay(inputs, ctx, [0, 2], [0, 30], groupScratch);
        ((long)Math.Round(key2.Utility * WeightedObjectiveInputs.Scale)).Should().Be(replay2);

        // 4. Push node 3 (Natural attraction) - shares group with node 2
        buffers.Sequence[2] = 3;
        buffers.Departure[2] = 90;
        buffers.Contains[3] = true;
        kernel.OnPush(2, 3, 60, ctx.Nodes[2].MatrixIndex);

        var key3 = kernel.PeekEliteKey(3, 90, ctx.Nodes[3].MatrixIndex, 0, 90);
        var replay3 = ObjectiveCalculator.Replay(inputs, ctx, [0, 2, 3], [0, 30, 60], groupScratch);
        ((long)Math.Round(key3.Utility * WeightedObjectiveInputs.Scale)).Should().Be(replay3);
    }

    [Fact]
    public void Incremental_Replay_And_Evaluate_AreEqual_ForTheSameRoute()
    {
        var loved = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { PoiCategoryNames.Culture };
        var (ctx, inputs) = CreateScenario([
            ("Pagoda", PoiCategoryNames.Culture),
            ("Museum", PoiCategoryNames.Museum),
            ("Park", PoiCategoryNames.Nature),
            ("Mountain", PoiCategoryNames.NaturalAttraction)
        ], lovedCategories: loved);

        var evaluator = new ItineraryScheduleEvaluator(Options);
        var candidatesList = ctx.Input.Candidates.ToList();
        var schedule = evaluator.Evaluate(
            ctx.Input,
            candidatesList,
            RouteDurationMatrix.Create(new int[ctx.Nodes.Count + 2, ctx.Nodes.Count + 2]),
            candidatesList,
            ctx.CandidateMatrixIndices);

        schedule.Should().NotBeNull();

        var nodeOf = ctx.Nodes
            .Select((node, index) => (node.Candidate.Id, Index: index))
            .ToDictionary(pair => pair.Id, pair => pair.Index);
        var groupScratch = new int[inputs.DiversityGroupCount];
        var itemScratch = new List<GeneratedItineraryItem>();
        var visitItems = schedule!.Plan.Items
            .Where(item => item.Kind == ItineraryItemKind.Visit)
            .OrderBy(item => item.SequenceNo)
            .ToArray();
        var sequence = visitItems.Select(item => nodeOf[item.PointOfInterestId!.Value]).ToArray();
        var starts = visitItems
            .Select(item => ObjectiveCalculator.ToModelMinute(item.PlannedArrivalUtc, ctx.Input.StartAtUtc))
            .ToArray();

        var n = ctx.Nodes.Count;
        var buffers = new SearchBuffers(n, n);
        var initialDomain = Enumerable.Range(0, n).ToArray();
        var pre = new SearchPrecomputed(
            new int[n], new int[n], new int[n], new int[n], new int[n],
            0, 100, initialDomain, Enumerable.Repeat(true, n).ToArray());
        var kernel = new WeightedUtilityKernel(inputs, new CspOptions { Mode = ObjectiveMode.WeightedUtility });
        kernel.Initialize(ctx, buffers, pre, new CspStatistics());

        var previousMatrix = ctx.StartMatrixIndex;
        for (var depth = 0; depth < sequence.Length; depth++)
        {
            var node = sequence[depth];
            buffers.Sequence[depth] = node;
            buffers.Departure[depth] = starts[depth] + ctx.Nodes[node].VisitMinutes;
            buffers.Contains[node] = true;
            kernel.OnPush(depth, node, starts[depth], previousMatrix);
            previousMatrix = ctx.Nodes[node].MatrixIndex;
        }

        var incrementalJ = (long)Math.Round(
            kernel.PeekEliteKey(
                sequence.Length,
                buffers.Departure[sequence.Length - 1],
                previousMatrix,
                travel: 0,
                finish: buffers.Departure[sequence.Length - 1]).Utility
            * WeightedObjectiveInputs.Scale);

        var evaluatedJ = ObjectiveCalculator.Evaluate(
            inputs, ctx, schedule, nodeOf, ctx.CandidateMatrixIndices, groupScratch, itemScratch);
        var replayedJ = ObjectiveCalculator.Replay(inputs, ctx, sequence, starts, groupScratch);

        incrementalJ.Should().Be(replayedJ);
        replayedJ.Should().Be(evaluatedJ);
    }
}