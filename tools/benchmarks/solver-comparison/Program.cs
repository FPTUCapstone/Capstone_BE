using System.Diagnostics;
using System.Globalization;
using System.Text;

using TripMate.Application.Features.Scheduling.Common;
using TripMate.Application.Features.Scheduling.Csp;
using TripMate.Application.UnitTests.Features.Scheduling.Fixtures;
using TripMate.Domain.Enums;

// UC-10 solver comparison: optimization disabled vs Heuristic vs CSP on identical synthetic inputs.
// Usage: dotnet run -c Release --project tools/benchmarks/solver-comparison -- [outputDirectory] [iterations] [seeds]
var outputDirectory = args.Length > 0 ? args[0] : Path.Combine("docs", "benchmarks");
var iterations = args.Length > 1 ? int.Parse(args[1], CultureInfo.InvariantCulture) : 3;
var seeds = args.Length > 2 ? int.Parse(args[2], CultureInfo.InvariantCulture) : 25;
var candidateCounts = new[] { 10, 20, 40 };
var mandatoryCounts = new[] { 0, 1, 3, 6 };
var date = DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

var scenarios = new List<Scenario>
{
    Named("zigzag", OptionalRouteOptimizationScenarios.CreateZigzagScenario()),
    Named("zigzag-240", OptionalRouteOptimizationScenarios.CreateZigzagScenario(240)),
    Named("tail-infeasible", OptionalRouteOptimizationScenarios.CreateTailInfeasibleMiddleFeasibleScenario()),
    Named("asymmetric", OptionalRouteOptimizationScenarios.CreateAsymmetricScenario()),
    Named("two-opt", OptionalRouteOptimizationScenarios.CreateTwoOptStrictImprovementScenario()),
    Named("relocate", OptionalRouteOptimizationScenarios.CreateRelocateStrictImprovementScenario()),
    Named("reconsideration", OptionalRouteOptimizationScenarios.CreateReconsiderationScenario()),
};
foreach (var candidates in candidateCounts)
{
    foreach (var mandatory in mandatoryCounts)
    {
        for (var seed = 1; seed <= seeds; seed++)
        {
            var (input, matrix) = OptionalRouteOptimizationScenarios.CreateSyntheticCorpusScenario(candidates, mandatory, seed);
            scenarios.Add(new Scenario($"c{candidates}-m{mandatory}", $"c{candidates}-m{mandatory}-s{seed}", input, matrix));
        }
    }
}

var disabledOptions = new SchedulingGenerationOptions { EnableOptionalRouteOptimization = false };
var heuristicOptions = new SchedulingGenerationOptions { SolverMode = SchedulingSolverMode.Heuristic };
var cspOptions = new SchedulingGenerationOptions { SolverMode = SchedulingSolverMode.Csp };

Console.WriteLine($"Running {scenarios.Count} scenarios × 3 modes × {iterations} iterations...");
var results = new List<ScenarioResult>(scenarios.Count);
foreach (var scenario in scenarios)
{
    var disabled = await Measure(scenario, disabledOptions, iterations);
    var heuristic = await Measure(scenario, heuristicOptions, iterations);
    var csp = await Measure(scenario, cspOptions, iterations);
    var statistics = CspStatisticsFor(scenario, cspOptions);
    var comparison = heuristic.Plan is not null && csp.Plan is not null
        ? Math.Sign(ProductRulePlanComparison.Compare(scenario.Input, scenario.Matrix, csp.Plan, heuristic.Plan))
        : (int?)null;
    results.Add(new ScenarioResult(scenario, disabled, heuristic, csp, comparison, statistics));
}

Directory.CreateDirectory(outputDirectory);
var baseName = $"UC-10-csp-solver-comparison-{date}";
var csvPath = Path.Combine(outputDirectory, baseName + ".csv");
File.WriteAllText(csvPath, Csv(results));
var markdownPath = Path.Combine(outputDirectory, baseName + ".md");
File.WriteAllText(markdownPath, Markdown(results, cspOptions.Csp, iterations, seeds, date, baseName + ".csv"));
Console.WriteLine($"Wrote {markdownPath} and {csvPath}");

static Scenario Named(string name, (GenerationInput Input, RouteDurationMatrix Matrix) fixture) =>
    new("named", name, fixture.Input, fixture.Matrix);

static async Task<ModeResult> Measure(Scenario scenario, SchedulingGenerationOptions options, int iterations)
{
    var service = new ItineraryGenerationService(new FixedMatrixProvider(scenario.Matrix), options: options);
    var warmup = await service.GenerateAsync(scenario.Input, CancellationToken.None);
    var timings = new List<double>(iterations);
    for (var i = 0; i < iterations; i++)
    {
        var watch = Stopwatch.StartNew();
        _ = await service.GenerateAsync(scenario.Input, CancellationToken.None);
        watch.Stop();
        timings.Add(watch.Elapsed.TotalMilliseconds);
    }

    return new ModeResult(warmup.IsSuccess ? warmup.Value : null, timings);
}

static CspStatistics CspStatisticsFor(Scenario scenario, SchedulingGenerationOptions options)
{
    var candidates = scenario.Input.Candidates.ToArray();
    var indices = candidates.Select((candidate, index) => (candidate.Id, Index: index + 1))
        .ToDictionary(pair => pair.Id, pair => pair.Index);
    return new CspItinerarySolver(new ItineraryScheduleEvaluator(options), options)
        .Solve(scenario.Input, scenario.Matrix, candidates, indices)
        .Statistics;
}

static int OptionalCount(Scenario scenario, GeneratedItineraryPlan? plan) =>
    plan?.Items.Count(item => item.Kind == ItineraryItemKind.Visit
        && item.PointOfInterestId is { } id
        && !scenario.Input.MandatoryPoiIds.Contains(id)) ?? 0;

static int? Travel(Scenario scenario, GeneratedItineraryPlan? plan) =>
    plan is null ? null : ProductRulePlanComparison.MatrixTravelMinutes(scenario.Input, scenario.Matrix, plan);

static double Percentile(IEnumerable<double> values, double percentile)
{
    var sorted = values.OrderBy(value => value).ToArray();
    return sorted.Length == 0 ? 0 : sorted[Math.Min(sorted.Length - 1, (int)(sorted.Length * percentile))];
}

static string Csv(IEnumerable<ScenarioResult> results)
{
    var csv = new StringBuilder(
        "segment,scenario,csp_vs_heuristic,heuristic_optional,csp_optional,heuristic_travel,csp_travel,"
        + "disabled_ms_max,heuristic_ms_max,csp_ms_max,csp_nodes,csp_node_limit,csp_time_limit,csp_fell_back_to_heuristic\n");
    foreach (var result in results)
    {
        csv.Append(CultureInfo.InvariantCulture,
            $"{result.Scenario.Segment},{result.Scenario.Name},{Verdict(result.Comparison)},"
            + $"{OptionalCount(result.Scenario, result.Heuristic.Plan)},{OptionalCount(result.Scenario, result.Csp.Plan)},"
            + $"{Travel(result.Scenario, result.Heuristic.Plan)},{Travel(result.Scenario, result.Csp.Plan)},"
            + $"{result.Disabled.Timings.Max():F2},{result.Heuristic.Timings.Max():F2},{result.Csp.Timings.Max():F2},"
            + $"{result.Statistics.NodesExpanded},{result.Statistics.NodeLimitReached},{result.Statistics.TimeLimitReached},"
            + $"{result.Csp.Plan is not null && result.Statistics.SolutionsFound == 0}\n");
    }

    return csv.ToString();
}

static string Verdict(int? comparison) => comparison switch
{
    < 0 => "better",
    0 => "equal",
    > 0 => "worse",
    null => "excluded",
};

static string Markdown(
    IReadOnlyList<ScenarioResult> results,
    CspOptions csp,
    int iterations,
    int seeds,
    string date,
    string csvName)
{
    var markdown = new StringBuilder();
    markdown.AppendLine(CultureInfo.InvariantCulture, $"# UC-10 CSP Solver Comparison — {date}");
    markdown.AppendLine();
    markdown.AppendLine("## Environment and corpus");
    markdown.AppendLine();
    markdown.AppendLine(CultureInfo.InvariantCulture, $"- Commit: `{GitCommit()}`");
    markdown.AppendLine(CultureInfo.InvariantCulture, $"- OS: {Environment.OSVersion}; logical processors: {Environment.ProcessorCount}; runtime: .NET {Environment.Version}");
    markdown.AppendLine(CultureInfo.InvariantCulture, $"- Runner: `tools/benchmarks/solver-comparison`, Release, 1 warmup + {iterations} measured iterations per mode");
    markdown.AppendLine(CultureInfo.InvariantCulture, $"- CSP options: MaxNodes {csp.MaxNodes}, TimeLimitMilliseconds {csp.TimeLimitMilliseconds}, MaxStops {csp.MaxStops}, MaxOptionalDomainSize {csp.MaxOptionalDomainSize}");
    markdown.AppendLine(CultureInfo.InvariantCulture, $"- Corpus: **synthetic only**. 7 named `OptionalRouteOptimizationScenarios` fixtures plus `CreateSyntheticCorpusScenario` for candidates {{10, 20, 40}} × mandatory {{0, 1, 3, 6}} × seeds 1–{seeds}; sample size {results.Count}.");
    markdown.AppendLine(CultureInfo.InvariantCulture, $"- Excluded (a solver produced no plan): {results.Count(result => result.Comparison is null)}");
    markdown.AppendLine("- Comparison rule: the product rule of `ScheduleGlobalComparator` (optional inclusion in canonical rank order, then matrix travel, duration, end time, visit IDs).");
    markdown.AppendLine();
    markdown.AppendLine("## Quality: CSP vs Heuristic under the product rule");
    markdown.AppendLine();
    markdown.AppendLine("| Segment | n | CSP better | Equal | CSP worse | Avg optional (H / CSP) | Avg travel min (H / CSP) |");
    markdown.AppendLine("|---|---:|---:|---:|---:|---:|---:|");
    foreach (var group in results.GroupBy(result => result.Scenario.Segment))
    {
        var included = group.Where(result => result.Comparison is not null).ToArray();
        markdown.AppendLine(CultureInfo.InvariantCulture,
            $"| {group.Key} | {group.Count()} | {included.Count(r => r.Comparison < 0)} | {included.Count(r => r.Comparison == 0)} | {included.Count(r => r.Comparison > 0)} "
            + $"| {included.Average(r => OptionalCount(r.Scenario, r.Heuristic.Plan)):F2} / {included.Average(r => OptionalCount(r.Scenario, r.Csp.Plan)):F2} "
            + $"| {included.Average(r => Travel(r.Scenario, r.Heuristic.Plan) ?? 0):F1} / {included.Average(r => Travel(r.Scenario, r.Csp.Plan) ?? 0):F1} |");
    }

    markdown.AppendLine();
    markdown.AppendLine("## Latency (generator only)");
    markdown.AppendLine();
    markdown.AppendLine("| Segment | Disabled p95 | Heuristic p95 | CSP p50 | CSP p95 | CSP max | CSP added p95 (gate ≤ 300 ms) | Node limit hit | Time limit hit | Fell back to heuristic |");
    markdown.AppendLine("|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|");
    foreach (var group in results.GroupBy(result => result.Scenario.Segment))
    {
        var disabledP95 = Percentile(group.SelectMany(r => r.Disabled.Timings), 0.95);
        var cspTimings = group.SelectMany(r => r.Csp.Timings).ToArray();
        var cspP95 = Percentile(cspTimings, 0.95);
        var added = cspP95 - disabledP95;
        markdown.AppendLine(CultureInfo.InvariantCulture,
            $"| {group.Key} | {disabledP95:F2} ms | {Percentile(group.SelectMany(r => r.Heuristic.Timings), 0.95):F2} ms "
            + $"| {Percentile(cspTimings, 0.5):F2} ms | {cspP95:F2} ms | {cspTimings.Max():F2} ms "
            + $"| {added:F2} ms {(added <= 300 ? "✅" : "❌")} | {group.Count(r => r.Statistics.NodeLimitReached)} | {group.Count(r => r.Statistics.TimeLimitReached)} | {group.Count(r => r.Csp.Plan is not null && r.Statistics.SolutionsFound == 0)} |");
    }

    var worse = results.Where(result => result.Comparison > 0).Select(result => result.Scenario.Name).ToArray();
    markdown.AppendLine();
    markdown.AppendLine("## Gate status");
    markdown.AppendLine();
    markdown.AppendLine(CultureInfo.InvariantCulture, $"- Quality (100% better or equal): {(worse.Length == 0 ? "passed" : $"**failed** in {worse.Length} scenarios")}");
    if (worse.Length > 0)
    {
        markdown.AppendLine(CultureInfo.InvariantCulture, $"- Scenarios where the CSP is worse: {string.Join(", ", worse.Take(40))}{(worse.Length > 40 ? ", …" : string.Empty)}");
    }

    markdown.AppendLine(CultureInfo.InvariantCulture, $"- Time limit reached in any run: {(results.Any(r => r.Statistics.TimeLimitReached) ? "yes" : "no")}");
    markdown.AppendLine();
    markdown.AppendLine("## Limitations");
    markdown.AppendLine();
    markdown.AppendLine("- Synthetic, generator-only evidence: no OpenRouteService, ranking, or database latency is included,");
    markdown.AppendLine("  and conclusions generalize only to the tested synthetic distribution.");
    markdown.AppendLine(CultureInfo.InvariantCulture, $"- Per-scenario raw results: `{csvName}`.");
    return markdown.ToString();
}

static string GitCommit()
{
    try
    {
        using var git = Process.Start(new ProcessStartInfo("git", "rev-parse --short HEAD")
        {
            RedirectStandardOutput = true,
            UseShellExecute = false,
        });
        var commit = git?.StandardOutput.ReadToEnd().Trim();
        git?.WaitForExit();
        return string.IsNullOrEmpty(commit) ? "unknown" : commit;
    }
    catch (System.ComponentModel.Win32Exception)
    {
        return "unknown";
    }
}

internal sealed record Scenario(string Segment, string Name, GenerationInput Input, RouteDurationMatrix Matrix);

internal sealed record ModeResult(GeneratedItineraryPlan? Plan, IReadOnlyList<double> Timings);

internal sealed record ScenarioResult(
    Scenario Scenario,
    ModeResult Disabled,
    ModeResult Heuristic,
    ModeResult Csp,
    int? Comparison,
    CspStatistics Statistics);

internal sealed class FixedMatrixProvider(RouteDurationMatrix matrix) : IRouteDurationProvider
{
    public Task<RouteDurationMatrix> GetMatrixAsync(
        IReadOnlyList<RoutePoint> points,
        TransportMode transportMode,
        CancellationToken cancellationToken) => Task.FromResult(matrix);
}