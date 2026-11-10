using TripMate.Application.UnitTests.Features.Scheduling.Fixtures;
using TripMate.Domain.Enums;

namespace TripMate.Application.UnitTests.Features.Scheduling.Benchmarks.Weighted;

/// <summary>One measured WeightedUtility generation of the ADR-002 switch gate (G4b).</summary>
public sealed record WeightedGateRun(
    string ScenarioId,
    int CandidateCount,
    string PersonaId,
    TravelerPace Pace,
    bool Feasible,
    int StopCount,
    long? ModelObjective,
    long? HeuristicObjective,
    long? GreedyObjective,
    string? Outcome,
    bool? SearchCompleted,
    bool NodeLimitReached,
    bool TimeLimitReached,
    int NodesExpanded,
    int EvaluatorCalls,
    double CspElapsedMs,
    IReadOnlyList<double> SamplesMs)
{
    /// <summary>Visited POIs of the served plan, in order (persona overlap, ADR-002 G4).</summary>
    public IReadOnlyList<long> VisitPoiIds { get; init; } = [];

    /// <summary>Visited optional POIs (mandatory stops are the same for every traveler).</summary>
    public IReadOnlyList<long> OptionalVisitPoiIds { get; init; } = [];

    /// <summary>Category of each visit, for the diversity check of travelers without a profile.</summary>
    public IReadOnlyList<string> VisitCategories { get; init; } = [];

    /// <summary>Minutes from the trip start to the return of the served plan, out of <see cref="AvailableMinutes"/>.</summary>
    public int PlanMinutes { get; init; }

    public int AvailableMinutes { get; init; }
}

public sealed record WeightedGateLatency(
    int CandidateCount,
    int Runs,
    double P50Milliseconds,
    double P95Milliseconds,
    double P99Milliseconds,
    double MaxMilliseconds,
    int TimeLimitRuns,
    int NodeLimitRuns,
    int SearchCompletedRuns);

/// <summary>Pairwise Jaccard of optional stops between personas at one pace.</summary>
public sealed record WeightedGatePaceOverlap(TravelerPace Pace, int Pairs, double MeanJaccard, double MaxJaccard);

public sealed record WeightedGatePaceStops(
    TravelerPace Pace,
    int Runs,
    int InfeasibleRuns,
    double MeanStops,
    int MinStops,
    int MaxStops,
    int TargetMin,
    int TargetMax)
{
    public bool WithinTarget => Runs > 0 && MeanStops >= TargetMin && MeanStops <= TargetMax;
}

/// <summary>
/// ADR-002 §4 gates computed from measured runs. Latency runs cover the c10–c40 corpus at the shipped limits; pace runs
/// are 8-hour trips, where D2 sets the stop targets (Relaxed 3 ± 1, Moderate 4–6, Fast 6–8).
/// </summary>
public sealed record WeightedGateSummary(
    IReadOnlyList<WeightedGateLatency> LatencyBySize,
    double P95Milliseconds,
    double P99Milliseconds,
    double MaxMilliseconds,
    int TimeLimitRuns,
    IReadOnlyList<WeightedGatePaceStops> StopsByPace,
    int DominanceRuns,
    int DominanceViolations,
    IReadOnlyList<WeightedGatePaceOverlap> OverlapByPace)
{
    public const double MaxMeanJaccard = 0.4;

    /// <summary>
    /// ADR-002 §4 (amended 2026-11-11): p95 ≤ 300 ms below 40 candidates. At 40 the limit is the 800 ms rollback threshold
    /// (ADR-001 §4): the slow tail is dense mandatory input, and a smaller node budget lowers J for every traveler.
    /// </summary>
    public static double P95LimitMilliseconds(int candidateCount) => candidateCount >= 40 ? 800 : 300;

    public static readonly IReadOnlyDictionary<TravelerPace, (int Min, int Max)> StopTargets =
        new Dictionary<TravelerPace, (int Min, int Max)>
        {
            [TravelerPace.Relaxed] = (2, 4),
            [TravelerPace.Moderate] = (4, 6),
            [TravelerPace.Fast] = (6, 8),
        };

    /// <summary>Every candidate size, like the Phase 3 per-segment gate: a fast majority must not hide a slow size.</summary>
    public bool LatencyPassed => LatencyBySize.All(size => size.P95Milliseconds <= P95LimitMilliseconds(size.CandidateCount));

    public bool NoTimeLimit => TimeLimitRuns == 0;

    public bool PaceTargetsPassed => StopsByPace.All(pace => pace.WithinTarget);

    /// <summary>Mean stops never decrease from Relaxed to Moderate to Fast.</summary>
    public bool PaceOrderPassed => StopsByPace
        .OrderBy(pace => pace.Pace)
        .Zip(StopsByPace.OrderBy(pace => pace.Pace).Skip(1))
        .All(pair => pair.First.MeanStops <= pair.Second.MeanStops);

    /// <summary>ADR-002 §4.4: by J, the served plan scores at least the heuristic plan and the greedy incumbent.</summary>
    public bool NotWorseThanBaselines => DominanceViolations == 0;

    /// <summary>PLAN G4: at every pace, personas with different interests share on average less than 40% of optional stops.</summary>
    public bool PersonasDistinct => OverlapByPace.All(pace => pace.MeanJaccard < MaxMeanJaccard);

    public bool Passed => LatencyPassed && NoTimeLimit && PaceTargetsPassed && PaceOrderPassed && NotWorseThanBaselines && PersonasDistinct;

    public static WeightedGateSummary Create(
        IReadOnlyList<WeightedGateRun> latencyRuns,
        IReadOnlyList<WeightedGateRun> paceRuns)
    {
        ArgumentNullException.ThrowIfNull(latencyRuns);
        ArgumentNullException.ThrowIfNull(paceRuns);

        var latency = latencyRuns
            .GroupBy(run => run.CandidateCount)
            .OrderBy(group => group.Key)
            .Select(group =>
            {
                var samples = group.SelectMany(run => run.SamplesMs).ToArray();
                return new WeightedGateLatency(
                    group.Key,
                    group.Count(),
                    SolverComparisonReport.Percentile(samples, 0.50),
                    SolverComparisonReport.Percentile(samples, 0.95),
                    SolverComparisonReport.Percentile(samples, 0.99),
                    samples.Max(),
                    group.Count(run => run.TimeLimitReached),
                    group.Count(run => run.NodeLimitReached),
                    group.Count(run => run.SearchCompleted == true));
            })
            .ToArray();
        var allSamples = latencyRuns.SelectMany(run => run.SamplesMs).ToArray();
        var dominance = latencyRuns.Concat(paceRuns).Where(run => run.ModelObjective.HasValue).ToArray();

        var stops = paceRuns
            .GroupBy(run => run.Pace)
            .OrderBy(group => group.Key)
            .Select(group =>
            {
                var feasible = group.Where(run => run.Feasible).ToArray();
                var (min, max) = StopTargets[group.Key];
                return new WeightedGatePaceStops(
                    group.Key,
                    feasible.Length,
                    group.Count() - feasible.Length,
                    feasible.Length == 0 ? 0 : feasible.Average(run => run.StopCount),
                    feasible.Length == 0 ? 0 : feasible.Min(run => run.StopCount),
                    feasible.Length == 0 ? 0 : feasible.Max(run => run.StopCount),
                    min,
                    max);
            })
            .ToArray();

        return new WeightedGateSummary(
            latency,
            SolverComparisonReport.Percentile(allSamples, 0.95),
            SolverComparisonReport.Percentile(allSamples, 0.99),
            allSamples.Length == 0 ? 0 : allSamples.Max(),
            latencyRuns.Count(run => run.TimeLimitReached) + paceRuns.Count(run => run.TimeLimitReached),
            stops,
            dominance.Length,
            dominance.Count(run => run.ModelObjective < run.HeuristicObjective || run.ModelObjective < run.GreedyObjective),
            Overlap(paceRuns));
    }

    private static WeightedGatePaceOverlap[] Overlap(IReadOnlyList<WeightedGateRun> paceRuns) =>
        paceRuns
            .Where(run => run.Feasible)
            .GroupBy(run => run.Pace)
            .OrderBy(group => group.Key)
            .Select(group =>
            {
                var runs = group.ToArray();
                var jaccards = new List<double>();
                for (var i = 0; i < runs.Length; i++)
                {
                    for (var j = i + 1; j < runs.Length; j++)
                    {
                        jaccards.Add(Jaccard(runs[i].OptionalVisitPoiIds, runs[j].OptionalVisitPoiIds));
                    }
                }

                return new WeightedGatePaceOverlap(
                    group.Key,
                    jaccards.Count,
                    jaccards.Count == 0 ? 0 : jaccards.Average(),
                    jaccards.Count == 0 ? 0 : jaccards.Max());
            })
            .ToArray();

    /// <summary>Two plans without optional stops share nothing personal, so their overlap is 0.</summary>
    private static double Jaccard(IReadOnlyList<long> left, IReadOnlyList<long> right)
    {
        var union = left.Union(right).Count();
        return union == 0 ? 0 : (double)left.Intersect(right).Count() / union;
    }
}