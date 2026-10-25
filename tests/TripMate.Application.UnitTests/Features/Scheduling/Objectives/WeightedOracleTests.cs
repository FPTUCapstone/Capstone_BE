using TripMate.Application.Features.Scheduling.Common;
using TripMate.Application.Features.Scheduling.Csp;
using TripMate.Application.Features.Scheduling.Csp.Objectives;
using TripMate.Application.Features.Scheduling.Personalization;
using TripMate.Application.Features.Scheduling.TimeOfDay;
using TripMate.Application.UnitTests.Features.Scheduling.Fixtures;
using TripMate.Domain.Constants;
using TripMate.Domain.Enums;

using Xunit;

namespace TripMate.Application.UnitTests.Features.Scheduling.Objectives;

public class WeightedOracleTests
{
    private static readonly SchedulingGenerationOptions Options = new();
    private static readonly string[] AvailableCategories =
    [
        PoiCategoryNames.Beach,
        PoiCategoryNames.Culture,
        PoiCategoryNames.Museum,
        PoiCategoryNames.Nature,
        PoiCategoryNames.FoodAndDrink,
    ];

    public static IEnumerable<object[]> GenerateOracleInstances()
    {
        var rng = new Random(20261019);
        for (var i = 0; i < 300; i++)
        {
            yield return new object[] { i, rng.Next() };
        }
    }

    [Theory]
    [MemberData(nameof(GenerateOracleInstances))]
    public void BranchAndBound_Matches_BruteForce_Oracle(int instanceIndex, int seed)
    {
        var instanceRng = new Random(seed);
        var poiCount = instanceRng.Next(6, 11); // 6 to 10 POIs
        var mandatoryCount = instanceRng.Next(1, Math.Min(4, poiCount)); // 1 to 3
        var maxStops = instanceRng.Next(3, Math.Min(7, poiCount + 1)); // 3 to 6

        var isTightBudget = (instanceIndex % 4) == 0; // 1/4 tight budget

        var candidates = new List<GenerationCandidate>(poiCount);
        var baseByPoiId = new Dictionary<long, double>(poiCount);
        var points = new List<(double X, double Y)> { (0, 0) }; // 0: Start

        var assignedCategories = new List<string>();

        for (var i = 0; i < poiCount; i++)
        {
            var poiId = 1000L + i;
            var isFar = instanceRng.NextDouble() < 0.30; // 30% far away
            double x, y, baseScore;

            if (isFar)
            {
                var angle = instanceRng.NextDouble() * 2 * Math.PI;
                var dist = 40.0 + instanceRng.NextDouble() * 40.0; // 40 to 80
                x = dist * Math.Cos(angle);
                y = dist * Math.Sin(angle);
                baseScore = 0.10 + 0.20 * instanceRng.NextDouble(); // low base 0.10 - 0.30
            }
            else
            {
                x = (instanceRng.NextDouble() - 0.5) * 40.0; // -20 to 20
                y = (instanceRng.NextDouble() - 0.5) * 40.0;
                baseScore = 0.40 + 0.55 * instanceRng.NextDouble(); // 0.40 - 0.95
            }

            points.Add((x, y));
            baseByPoiId[poiId] = Math.Round(baseScore, 4);

            string category;
            if (assignedCategories.Count > 0 && instanceRng.NextDouble() < 0.20)
            {
                // 20% duplicate category
                category = assignedCategories[instanceRng.Next(assignedCategories.Count)];
            }
            else
            {
                category = AvailableCategories[instanceRng.Next(AvailableCategories.Length)];
            }
            assignedCategories.Add(category);

            var visitMinutes = instanceRng.Next(2, 6) * 15; // 30, 45, 60, 75
            var cost = instanceRng.Next(1, 6) * 10_000m; // 10k - 50k

            candidates.Add(OptionalRouteOptimizationScenarios.CreateCandidate(
                poiId, $"POI_{poiId}", visitMinutes, cost, (decimal)baseScore) with
            {
                CategoryName = category,
                HasShelter = instanceRng.NextDouble() > 0.3
            });
        }

        points.Add((5, 5)); // End matrix index

        var mandatoryPoiIds = candidates
            .OrderBy(_ => instanceRng.Next())
            .Take(mandatoryCount)
            .Select(c => c.Id)
            .ToList();

        decimal? budget = null;
        if (isTightBudget)
        {
            var mandatorySum = candidates.Where(c => mandatoryPoiIds.Contains(c.Id)).Sum(c => c.EstimatedVisitCost ?? 0m);
            budget = mandatorySum + 20_000m;
        }

        var input = OptionalRouteOptimizationScenarios.CreateInput(
            availableMinutes: 480,
            candidates,
            mandatoryPoiIds,
            restPreference: RestPreference.None,
            budgetVnd: budget);

        // Build duration matrix
        var totalPoints = points.Count;
        var matrixData = new int[totalPoints, totalPoints];
        for (var r = 0; r < totalPoints; r++)
        {
            for (var c = 0; c < totalPoints; c++)
            {
                if (r == c)
                {
                    matrixData[r, c] = 0;
                }
                else
                {
                    var dx = points[r].X - points[c].X;
                    var dy = points[r].Y - points[c].Y;
                    var dist = Math.Sqrt(dx * dx + dy * dy);
                    matrixData[r, c] = Math.Max(1, (int)Math.Round(dist));
                }
            }
        }
        var matrix = RouteDurationMatrix.Create(matrixData);

        var candidateIndices = candidates.Select((c, idx) => (c.Id, Index: idx + 1)).ToDictionary(x => x.Id, x => x.Index);

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
            LovedCategories: new HashSet<string> { PoiCategoryNames.Beach, PoiCategoryNames.Culture },
            RhoOutdoor: 0.25,
            RhoRest: 0.20,
            RhoStops: 0.30,
            RhoEffortPerHour: 0.05,
            ConfigVersion: "oracle-test");

        var requestInputs = new WeightedRequestInputs(
            baseByPoiId,
            personalParams,
            new RealTimeFitProviderFactory(personalParams));

        var evaluator = new ItineraryScheduleEvaluator(Options);

        // 1. Solve with Branch & Bound (DisableBound = false)
        var cspOptionsBnB = new CspOptions
        {
            Mode = ObjectiveMode.WeightedUtility,
            DisableBound = false,
            MaxStops = maxStops,
            MaxNodes = 500_000,
            TimeLimitMilliseconds = 120_000
        };
        var solverBnB = new CspItinerarySolver(evaluator, Options, cspOptionsBnB);
        var resultBnB = solverBnB.Solve(
            input, matrix, candidates, candidateIndices,
            initialIncumbent: null, weighted: requestInputs, cancellationToken: CancellationToken.None);

        // 2. Solve with Brute Force (DisableBound = true)
        var cspOptionsBf = new CspOptions
        {
            Mode = ObjectiveMode.WeightedUtility,
            DisableBound = true,
            MaxStops = maxStops,
            MaxNodes = 500_000,
            TimeLimitMilliseconds = 120_000
        };
        var solverBf = new CspItinerarySolver(evaluator, Options, cspOptionsBf);
        var resultBf = solverBf.Solve(
            input, matrix, candidates, candidateIndices,
            initialIncumbent: null, weighted: requestInputs, cancellationToken: CancellationToken.None);

        // Assertions
        Assert.True(resultBnB.Statistics.SearchCompleted, $"Instance {instanceIndex} (seed {seed}): B&B must complete search without reaching node/time limit");
        Assert.True(resultBf.Statistics.SearchCompleted, $"Instance {instanceIndex} (seed {seed}): Brute force must complete search without reaching node/time limit");

        var bnbSeq = resultBnB.Schedule?.VisitPoiIds ?? (IReadOnlyList<long>)[];
        var bfSeq = resultBf.Schedule?.VisitPoiIds ?? (IReadOnlyList<long>)[];

        var bnbBestJ = resultBnB.Statistics.SolutionsFound > 0 && resultBnB.Schedule != null ? 1 : 0;
        var bfBestJ = resultBf.Statistics.SolutionsFound > 0 && resultBf.Schedule != null ? 1 : 0;

        Assert.True(bnbBestJ == bfBestJ, $"Instance {instanceIndex} (seed {seed}): Feasibility mismatch. B&B found {resultBnB.Statistics.SolutionsFound}, BF found {resultBf.Statistics.SolutionsFound}");

        if (bnbSeq.Count > 0 || bfSeq.Count > 0)
        {
            Assert.True(
                bnbSeq.SequenceEqual(bfSeq),
                $"Instance {instanceIndex} (seed {seed}): Sequence mismatch. B&B: [{string.Join(',', bnbSeq)}], BF: [{string.Join(',', bfSeq)}]");
        }
    }
}