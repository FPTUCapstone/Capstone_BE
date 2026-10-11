using TripMate.Application.Features.Scheduling.Common;
using TripMate.Domain.Enums;

namespace TripMate.Application.UnitTests.Features.Scheduling.Fixtures;

public static class OptionalRouteOptimizationScenarios
{
    public static readonly DateTimeOffset StartAtUtc = new(2026, 10, 20, 1, 0, 0, TimeSpan.Zero);
    public static readonly TimeZoneInfo LocalTimeZone = TimeZoneInfo.FindSystemTimeZoneById("Asia/Ho_Chi_Minh");

    public static readonly GenerationOpeningHours[] DefaultPermissiveHours =
    [
        new(2, new TimeOnly(6, 0), new TimeOnly(22, 0)),
    ];

    public static GenerationCandidate CreateCandidate(
        long id,
        string name,
        int visitDurationMinutes,
        decimal? cost,
        decimal effectiveDesirabilityScore,
        decimal? scenicScoreForRanking = null,
        decimal? photoRatingForRanking = null,
        decimal? estimatedVisitCostForRanking = null,
        GenerationOpeningHours[]? openingHours = null,
        decimal latitudeOffset = 0m,
        decimal longitudeOffset = 0m) =>
        new(
            id,
            name,
            new RoutePoint(16.0544m + latitudeOffset, 108.2022m + longitudeOffset),
            visitDurationMinutes,
            cost,
            openingHours ?? DefaultPermissiveHours,
            effectiveDesirabilityScore,
            effectiveDesirabilityScore,
            scenicScoreForRanking,
            photoRatingForRanking,
            estimatedVisitCostForRanking ?? cost);

    public static GenerationInput CreateInput(
        int availableMinutes,
        IReadOnlyCollection<GenerationCandidate> candidates,
        IReadOnlyCollection<long> mandatoryPoiIds,
        RestPreference restPreference = RestPreference.None,
        decimal? budgetVnd = 200_000m) =>
        new(
            StartAtUtc,
            LocalTimeZone,
            new RoutePoint(16.0544m, 108.2022m),
            new RoutePoint(16.0600m, 108.2300m),
            availableMinutes,
            TransportMode.Motorbike,
            restPreference,
            budgetVnd,
            candidates,
            mandatoryPoiIds);

    /// <summary>
    /// Scenario where legacy tail-append causes extreme zigzag:
    /// Points: Start(0), M1(1), M2(2), Opt1(3), Opt2(4), End(5).
    /// M1 is near Start and Opt1. M2 is near Opt2 and End.
    /// In ranking order, Opt1 has highest desirability, then Opt2.
    /// Tail-append: 0 -> 1 -> 2 -> 3 (far backtrack!) -> 4 -> 5.
    /// Insertion: 0 -> 1 -> 3 -> 2 -> 4 -> 5.
    /// </summary>
    public static (GenerationInput Input, RouteDurationMatrix Matrix) CreateZigzagScenario(int availableMinutes = 480)
    {
        var candidates = new[]
        {
            CreateCandidate(101, "M1_NearStart", 30, 20_000m, 0.5m),
            CreateCandidate(102, "M2_NearEnd", 30, 20_000m, 0.5m),
            CreateCandidate(201, "Opt1_NearM1", 30, 10_000m, 0.95m),
            CreateCandidate(202, "Opt2_NearM2", 30, 10_000m, 0.85m),
        };

        var input = CreateInput(
            availableMinutes,
            candidates,
            mandatoryPoiIds: [101, 102]);

        // Points: 0:Start, 1:M1, 2:M2, 3:Opt1, 4:Opt2, 5:End
        var matrix = RouteDurationMatrix.Create(new int[,]
        {
            /* 0:Start */ { 0,  10, 60, 12, 65, 70 },
            /* 1:M1    */ { 10,  0, 50,  5, 55, 60 },
            /* 2:M2    */ { 60, 50,  0, 52,  6, 10 },
            /* 3:Opt1  */ { 12,  5, 52,  0, 55, 60 },
            /* 4:Opt2  */ { 65, 55,  6, 55,  0,  8 },
            /* 5:End   */ { 70, 60, 10, 60,  8,  0 },
        });

        return (input, matrix);
    }

    /// <summary>
    /// Scenario where an optional candidate is infeasible when appended at the tail,
    /// but easily fits when inserted between Start and Mandatory 1.
    /// Points: 0:Start, 1:M1(far), 2:Opt1(between Start and M1), 3:End(near M1).
    /// </summary>
    public static (GenerationInput Input, RouteDurationMatrix Matrix) CreateTailInfeasibleMiddleFeasibleScenario()
    {
        var candidates = new[]
        {
            CreateCandidate(101, "Mandatory_Far", visitDurationMinutes: 60, cost: 20_000m, effectiveDesirabilityScore: 0.5m),
            CreateCandidate(201, "Optional_Middle", visitDurationMinutes: 60, cost: 20_000m, effectiveDesirabilityScore: 0.9m),
        };

        // Total available time: 180 minutes.
        // If 0 -> 1 -> 2 -> 3:
        // 0 -> 1: 50 min + 60 min visit = 110 min.
        // 1 -> 2: 50 min -> arrive at 160 min + 60 min visit = 220 min > 180 min (IN突出 FEASIBLE).
        // If 0 -> 2 -> 1 -> 3:
        // 0 -> 2: 15 min + 60 min visit = 75 min.
        // 2 -> 1: 20 min -> arrive at 95 min + 60 min visit = 155 min.
        // 1 -> 3: 10 min -> arrive at End at 165 min <= 180 min (FEASIBLE!).
        var input = CreateInput(
            availableMinutes: 200,
            candidates,
            mandatoryPoiIds: [101]);

        var matrix = RouteDurationMatrix.Create(new int[,]
        {
            /* 0:Start */ { 0,  50, 15, 60 },
            /* 1:M1    */ { 50,  0, 50, 10 },
            /* 2:Opt1  */ { 15, 20,  0, 55 },
            /* 3:End   */ { 60, 10, 55,  0 },
        });

        return (input, matrix);
    }

    /// <summary>
    /// Scenario with asymmetric travel times (e.g. one-way streets).
    /// travel(A, B) != travel(B, A).
    /// </summary>
    public static (GenerationInput Input, RouteDurationMatrix Matrix) CreateAsymmetricScenario()
    {
        var candidates = new[]
        {
            CreateCandidate(101, "M1", visitDurationMinutes: 30, cost: 10_000m, effectiveDesirabilityScore: 0.5m),
            CreateCandidate(201, "Opt1", visitDurationMinutes: 30, cost: 10_000m, effectiveDesirabilityScore: 0.9m),
            CreateCandidate(202, "Opt2", visitDurationMinutes: 30, cost: 10_000m, effectiveDesirabilityScore: 0.8m),
        };

        var input = CreateInput(
            availableMinutes: 300,
            candidates,
            mandatoryPoiIds: [101]);

        // Points: 0:Start, 1:M1, 2:Opt1, 3:Opt2, 4:End
        var matrix = RouteDurationMatrix.Create(new int[,]
        {
            /* 0:Start */ { 0,  10, 25, 40, 50 },
            /* 1:M1    */ { 10,  0, 12, 35, 45 },
            /* 2:Opt1  */ { 30, 45,  0,  8, 20 },
            /* 3:Opt2  */ { 50, 15, 40,  0, 15 },
            /* 4:End   */ { 50, 45, 20, 15,  0 },
        });

        return (input, matrix);
    }

    /// <summary>
    /// Generates a synthetic benchmark scenario with N candidates and M mandatory POIs. The rest preference does
    /// not change the generated candidates or matrix, so the same seed gives the same instance for every preference.
    /// </summary>
    public static (GenerationInput Input, RouteDurationMatrix Matrix) CreateSyntheticCorpusScenario(
        int candidateCount,
        int mandatoryCount,
        int seed = 42,
        RestPreference restPreference = RestPreference.None)
    {
        var random = new Random(seed);
        var candidates = new List<GenerationCandidate>(candidateCount);
        for (var i = 1; i <= candidateCount; i++)
        {
            var isMandatory = i <= mandatoryCount;
            var desirability = isMandatory
                ? 0.5m
                : 0.99m - (i * 0.02m);
            candidates.Add(CreateCandidate(
                id: i,
                name: $"POI_{i}",
                visitDurationMinutes: 30,
                cost: 15_000m,
                effectiveDesirabilityScore: Math.Max(0.01m, desirability),
                latitudeOffset: (decimal)(random.NextDouble() * 0.1 - 0.05),
                longitudeOffset: (decimal)(random.NextDouble() * 0.1 - 0.05)));
        }

        var mandatoryIds = candidates.Take(mandatoryCount).Select(c => c.Id).ToArray();
        var totalPoints = candidateCount + 2;
        var matrixData = new int[totalPoints, totalPoints];
        for (var i = 0; i < totalPoints; i++)
        {
            for (var j = 0; j < totalPoints; j++)
            {
                if (i == j)
                {
                    matrixData[i, j] = 0;
                }
                else
                {
                    // Distance between 5 and 45 minutes with slight asymmetry
                    var baseDist = random.Next(5, 40);
                    var asymmetry = random.Next(0, 6);
                    matrixData[i, j] = baseDist + asymmetry;
                }
            }
        }

        var matrix = RouteDurationMatrix.Create(matrixData);
        var input = CreateInput(
            availableMinutes: 600,
            candidates,
            mandatoryPoiIds: mandatoryIds,
            restPreference: restPreference);

        return (input, matrix);
    }

    /// <summary>
    /// Scenario where 2-opt segment reversal strictly improves travel time.
    /// Reversing [102, 103] in [101, 102, 103, 104] yields [101, 103, 102, 104], reducing travel from 115m to 25m.
    /// </summary>
    public static (GenerationInput Input, RouteDurationMatrix Matrix) CreateTwoOptStrictImprovementScenario()
    {
        var candidates = new[]
        {
            CreateCandidate(101, "P1", visitDurationMinutes: 30, cost: 10_000m, effectiveDesirabilityScore: 0.9m),
            CreateCandidate(102, "P2", visitDurationMinutes: 30, cost: 10_000m, effectiveDesirabilityScore: 0.8m),
            CreateCandidate(103, "P3", visitDurationMinutes: 30, cost: 10_000m, effectiveDesirabilityScore: 0.7m),
            CreateCandidate(104, "P4", visitDurationMinutes: 30, cost: 10_000m, effectiveDesirabilityScore: 0.6m),
        };

        var input = CreateInput(
            availableMinutes: 300,
            candidates,
            mandatoryPoiIds: []);

        // Points: 0:Start, 1:P1, 2:P2, 3:P3, 4:P4, 5:End
        var matrix = RouteDurationMatrix.Create(new int[,]
        {
            /* 0:Start */ {  0,  5, 50, 50, 45, 50 },
            /* 1:P1    */ {  5,  0, 20, 10, 45, 50 },
            /* 2:P2    */ { 50, 50,  0,  5,  5, 25 },
            /* 3:P3    */ { 50, 50,  5,  0, 40,  5 },
            /* 4:P4    */ { 45, 45, 45, 45,  0,  5 },
            /* 5:End   */ { 50, 50, 25,  5,  5,  0 },
        });

        return (input, matrix);
    }

    /// <summary>
    /// Scenario where relocate strictly improves travel time and 2-opt alone cannot.
    /// From [101, 102, 103, 104], relocating 102 to tail produces [101, 103, 104, 102] with travel 25m,
    /// while all 2-opt reversals cost >= 110m (baseline is 70m).
    /// </summary>
    public static (GenerationInput Input, RouteDurationMatrix Matrix) CreateRelocateStrictImprovementScenario()
    {
        var candidates = new[]
        {
            CreateCandidate(101, "P1", visitDurationMinutes: 30, cost: 10_000m, effectiveDesirabilityScore: 0.9m),
            CreateCandidate(102, "P2", visitDurationMinutes: 30, cost: 10_000m, effectiveDesirabilityScore: 0.8m),
            CreateCandidate(103, "P3", visitDurationMinutes: 30, cost: 10_000m, effectiveDesirabilityScore: 0.7m),
            CreateCandidate(104, "P4", visitDurationMinutes: 30, cost: 10_000m, effectiveDesirabilityScore: 0.6m),
        };

        var input = CreateInput(
            availableMinutes: 300,
            candidates,
            mandatoryPoiIds: []);

        // Points: 0:Start, 1:P1, 2:P2, 3:P3, 4:P4, 5:End
        var matrix = RouteDurationMatrix.Create(new int[,]
        {
            /* 0:Start */ {  0,  5, 50, 50, 50, 50 },
            /* 1:P1    */ {  5,  0, 30,  5, 50, 50 },
            /* 2:P2    */ { 50, 50,  0, 20, 50,  5 },
            /* 3:P3    */ { 50, 50, 50,  0,  5,  5 },
            /* 4:P4    */ { 50, 50,  5, 50,  0,  5 },
            /* 5:End   */ { 50, 50,  5,  5,  5,  0 },
        });

        return (input, matrix);
    }

    /// <summary>
    /// Scenario where an optional candidate (205) is initially skipped due to tight time budget,
    /// but after local improvement (2-opt) reduces travel time of the admitted set, candidate 205 is
    /// reconsidered and admitted into the itinerary.
    /// </summary>
    public static (GenerationInput Input, RouteDurationMatrix Matrix) CreateReconsiderationScenario()
    {
        var candidates = new[]
        {
            CreateCandidate(101, "P1", visitDurationMinutes: 30, cost: 10_000m, effectiveDesirabilityScore: 0.95m),
            CreateCandidate(102, "P2", visitDurationMinutes: 30, cost: 10_000m, effectiveDesirabilityScore: 0.85m),
            CreateCandidate(103, "P3", visitDurationMinutes: 30, cost: 10_000m, effectiveDesirabilityScore: 0.75m),
            CreateCandidate(104, "P4", visitDurationMinutes: 30, cost: 10_000m, effectiveDesirabilityScore: 0.65m),
            CreateCandidate(205, "Opt5", visitDurationMinutes: 20, cost: 10_000m, effectiveDesirabilityScore: 0.55m),
        };

        var input = CreateInput(
            availableMinutes: 260,
            candidates,
            mandatoryPoiIds: []);

        // Points: 0:Start, 1:P1, 2:P2, 3:P3, 4:P4, 5:Opt5, 6:End
        var matrix = RouteDurationMatrix.Create(new int[,]
        {
            /* 0:Start */ {  0,  5, 50, 50, 45, 50, 50 },
            /* 1:P1    */ {  5,  0, 20, 10, 45, 50, 50 },
            /* 2:P2    */ { 50, 50,  0,  5,  5, 50, 25 },
            /* 3:P3    */ { 50, 50,  5,  0, 40, 50,  5 },
            /* 4:P4    */ { 45, 45, 45, 45,  0,  5,  5 },
            /* 5:Opt5  */ { 50, 50, 50, 50,  5,  0,  5 },
            /* 6:End   */ { 50, 50, 25,  5,  5,  5,  0 },
        });

        return (input, matrix);
    }
}