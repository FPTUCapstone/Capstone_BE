using TripMate.Application.Features.Scheduling.Common;
using TripMate.Application.Features.Scheduling.Csp;
using TripMate.Application.Features.Scheduling.Routing;
using TripMate.Application.UnitTests.Features.Scheduling.Fixtures;
using TripMate.Domain.Enums;

namespace TripMate.Application.UnitTests.Features.Scheduling.Csp;

/// <summary>
/// Brute-force comparison on randomized small instances: asymmetric, non-metric matrices,
/// tight opening windows, budgets. Complements the 12 fixed seeds in CspItinerarySolverTests.
/// </summary>
public class CspBruteForceTests
{
    private static readonly SchedulingGenerationOptions Options = new();

    private static (GenerationInput Input, RouteDurationMatrix Matrix) Make(int seed, int maxCandidates, RestPreference rest)
    {
        var r = new Random(seed);
        var n = r.Next(4, maxCandidates + 1);
        var mandatory = r.Next(0, Math.Min(3, n - 1) + 1);
        var budgeted = r.Next(2) == 0;
        var candidates = Enumerable.Range(1, n).Select(i =>
        {
            var open = r.Next(8, 16);
            var close = Math.Min(23, open + r.Next(1, 7));
            var permissive = r.Next(3) == 0;
            return OptionalRouteOptimizationScenarios.CreateCandidate(
                i, $"P{i}", r.Next(1, 7) * 15, budgeted ? r.Next(1, 6) * 10_000m : 10_000m,
                Math.Max(0.01m, 0.99m - r.Next(0, 50) * 0.02m),
                openingHours: permissive
                    ? null
                    : [new GenerationOpeningHours(2, new TimeOnly(open, 0), new TimeOnly(close, 0))]);
        }).ToList();

        var size = n + 2;
        var minutes = new int[size, size];
        for (var i = 0; i < size; i++)
        {
            for (var j = 0; j < size; j++)
            {
                // About a quarter of the arcs are very short shortcuts, so the matrix is
                // asymmetric and violates the triangle inequality, like real road matrices.
                minutes[i, j] = i == j ? 0 : r.Next(4) == 0 ? r.Next(1, 4) : r.Next(5, 61);
            }
        }

        var input = OptionalRouteOptimizationScenarios.CreateInput(
            r.Next(2, 13) * 60,
            candidates,
            candidates.Take(mandatory).Select(c => c.Id).ToArray(),
            rest,
            budgeted ? r.Next(3, 10) * 20_000m : 500_000m);
        return (input, RouteDurationMatrix.Create(minutes));
    }

    private static (GenerationCandidate[] Candidates, Dictionary<long, int> Indices) Index(GenerationInput input)
    {
        var candidates = input.Candidates.ToArray();
        var indices = candidates.Select((c, i) => (c.Id, Index: i + 1)).ToDictionary(x => x.Id, x => x.Index);
        return (candidates, indices);
    }

    /// <summary>
    /// Best schedule under the product rule (ScheduleGlobalComparator) over every model-feasible sequence of at most
    /// <paramref name="maxStops"/> stops drawn from <paramref name="allowed"/>, each run through
    /// ItineraryScheduleEvaluator (which inserts the rest stops). Prefixes are never pruned.
    /// </summary>
    private static EvaluatedItinerarySchedule? BruteForce(
        GenerationInput input, RouteDurationMatrix matrix, RoutingContext ctx, int maxStops, HashSet<int> allowed) =>
        CspProductRuleOracle.Best(input, matrix, Options, ctx, maxStops, allowed);

    private static EvaluatedItinerarySchedule? Heuristic(GenerationInput input, RouteDurationMatrix matrix)
    {
        var (candidates, indices) = Index(input);
        var mandatory = input.MandatoryPoiIds.Select(id => candidates.Single(c => c.Id == id)).ToArray();
        return new OptionalRouteOptimizer(new ItineraryScheduleEvaluator(Options), Options)
            .Optimize(input, mandatory, matrix, candidates, indices)?.Schedule;
    }

    [Theory]
    [InlineData(0, 300)]
    [InlineData(300, 300)]
    public void Solve_MatchesBruteForce_OnRandomNonMetricInstances_WithoutRest(int firstSeed, int count)
    {
        var cspOptions = new CspOptions { MaxNodes = 20_000_000, TimeLimitMilliseconds = 120_000 };
        for (var seed = firstSeed + 1; seed <= firstSeed + count; seed++)
        {
            var (input, matrix) = Make(seed, 7, RestPreference.None);
            var (candidates, indices) = Index(input);
            var ctx = RoutingContext.Create(input, matrix, candidates, indices, Options, Options.MiniRouting);
            var expected = BruteForce(input, matrix, ctx, int.MaxValue, Enumerable.Range(0, ctx.Nodes.Count).ToHashSet());

            var result = new CspItinerarySolver(new ItineraryScheduleEvaluator(Options), Options, cspOptions)
                .Solve(input, matrix, candidates, indices);

            Assert.True(result.Statistics.SearchCompleted, $"seed {seed}: search did not complete");
            if (expected is null)
            {
                Assert.True(result.Schedule is null && result.Statistics.SolutionsFound == 0, $"seed {seed}: brute force says infeasible");
            }
            else
            {
                Assert.True(result.Schedule is not null, $"seed {seed}: CSP lost a solution");
                Assert.True(
                    CspProductRuleOracle.Comparer(input, matrix).Compare(result.Schedule, expected) == 0,
                    $"seed {seed}: csp [{string.Join(",", result.Schedule!.VisitPoiIds)}], optimum [{string.Join(",", expected.VisitPoiIds)}]");
            }
        }
    }

    [Fact]
    public void Solve_MatchesBruteForce_UnderRestrictiveStopsAndDomain()
    {
        for (var seed = 1; seed <= 400; seed++)
        {
            var (input, matrix) = Make(seed + 10_000, 8, RestPreference.None);
            var random = new Random(seed);
            var stops = random.Next(2, 5);
            var domain = random.Next(2, 7);
            var withHeuristicIncumbent = random.Next(2) == 0;
            var cspOptions = new CspOptions
            {
                MaxStops = stops,
                MaxOptionalDomainSize = domain,
                MaxNodes = 20_000_000,
                TimeLimitMilliseconds = 120_000,
            };
            var (candidates, indices) = Index(input);
            var ctx = RoutingContext.Create(input, matrix, candidates, indices, Options, Options.MiniRouting);
            var allowed = ctx.MandatoryNodes.Concat(CspProductRuleOracle.TopRankedOptionalNodes(input, matrix, ctx, domain)).ToHashSet();
            var expected = BruteForce(input, matrix, ctx, stops, allowed);

            var result = new CspItinerarySolver(new ItineraryScheduleEvaluator(Options), Options, cspOptions)
                .Solve(input, matrix, candidates, indices, withHeuristicIncumbent ? Heuristic(input, matrix) : null);

            Assert.True(result.Statistics.SearchCompleted, $"seed {seed}");
            if (expected is null)
            {
                Assert.Equal(0, result.Statistics.SolutionsFound);
                Assert.Null(result.Schedule);
            }
            else
            {
                Assert.NotNull(result.Schedule);
                Assert.True(CspProductRuleOracle.Comparer(input, matrix).Compare(result.Schedule, expected) == 0, $"seed {seed}");
                Assert.True(result.Schedule!.VisitPoiIds.Count <= stops, $"seed {seed}: over MaxStops");
            }
        }
    }

    [Fact]
    public void Solve_MatchesEvaluatorAwareOptimum_WithAutoAndFrequentRest()
    {
        var cspOptions = new CspOptions { MaxNodes = 20_000_000, TimeLimitMilliseconds = 120_000 };
        var worse = new List<string>();
        for (var seed = 1; seed <= 600; seed++)
        {
            var rest = seed % 2 == 0 ? RestPreference.Auto : RestPreference.Frequent;
            var (input, matrix) = Make(seed + 50_000, 7, rest);
            var (candidates, indices) = Index(input);
            var relaxed = RoutingContext.Create(input, matrix, candidates, indices, Options, Options.MiniRouting, reserveRestTime: false);
            var expected = BruteForce(input, matrix, relaxed, int.MaxValue, Enumerable.Range(0, relaxed.Nodes.Count).ToHashSet());
            var result = new CspItinerarySolver(new ItineraryScheduleEvaluator(Options), Options, cspOptions)
                .Solve(input, matrix, candidates, indices);
            if (expected is not null && result.Statistics.SearchCompleted
                && (result.Schedule is null || CspProductRuleOracle.Comparer(input, matrix).Compare(result.Schedule, expected) != 0))
            {
                worse.Add($"seed {seed} ({rest}): csp [{string.Join(",", result.Schedule?.VisitPoiIds ?? [])}], optimum [{string.Join(",", expected.VisitPoiIds)}]");
            }
        }

        Assert.True(worse.Count == 0, $"{worse.Count} instances worse than the evaluator-aware optimum:\n{string.Join('\n', worse.Take(10))}");
    }
}