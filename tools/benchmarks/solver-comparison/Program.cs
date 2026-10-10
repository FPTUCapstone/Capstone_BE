using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;

using TripMate.Application.Features.Scheduling.Common;
using TripMate.Application.UnitTests.Features.Scheduling.Fixtures;
using TripMate.Domain.Enums;

// UC-10 solver comparison: optimization disabled vs Heuristic vs CSP on identical synthetic inputs.
// Usage (from the repository root): dotnet run -c Release --project tools/benchmarks/solver-comparison -- [outputDirectory] [iterations] [seeds]
// The report records a SHA-256 of the benchmarked source files (SolverComparisonReport.SourceInputs), and
// SolverComparisonReportTests fails when the committed source no longer matches it, so commit the regenerated report
// together with the source it measured. Output: <base>.csv, <base>.timings.csv, <base>.meta.json and <base>.md, where the
// Markdown is recomputed from the three data files.
var positional = args.Where(arg => !arg.StartsWith("--", StringComparison.Ordinal)).ToArray();
var outputDirectory = positional.Length > 0 ? positional[0] : Path.Combine("docs", "benchmarks");
var iterations = positional.Length > 1 ? int.Parse(positional[1], CultureInfo.InvariantCulture) : 3;
var seeds = positional.Length > 2 ? int.Parse(positional[2], CultureInfo.InvariantCulture) : 25;
int[] candidateCounts = [10, 20, 40];
int[] mandatoryCounts = [0, 1, 3, 6];
RestPreference[] restPreferences = [RestPreference.None, RestPreference.Auto, RestPreference.Frequent];

var repositoryRoot = FindRepositoryRoot(Directory.GetCurrentDirectory());
if (repositoryRoot is null)
{
    Console.Error.WriteLine("Cannot find the repository root (TripMate.slnx); run from inside the repository.");
    return 1;
}

var source = SolverComparisonReport.ComputeSourceHash(repositoryRoot);

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
var namedScenarioCount = scenarios.Count;
foreach (var restPreference in restPreferences)
{
    var suffix = restPreference == RestPreference.None ? string.Empty : "-" + restPreference.ToString().ToLowerInvariant();
    foreach (var candidates in candidateCounts)
    {
        foreach (var mandatory in mandatoryCounts)
        {
            var segment = $"c{candidates}-m{mandatory}{suffix}";
            for (var seed = 1; seed <= seeds; seed++)
            {
                var (input, matrix) = OptionalRouteOptimizationScenarios.CreateSyntheticCorpusScenario(candidates, mandatory, seed, restPreference);
                scenarios.Add(new Scenario(segment, $"{segment}-s{seed}", input, matrix));
            }
        }
    }
}

var disabledOptions = new SchedulingGenerationOptions { EnableOptionalRouteOptimization = false };
var heuristicOptions = new SchedulingGenerationOptions { SolverMode = SchedulingSolverMode.Heuristic };
var cspOptions = new SchedulingGenerationOptions { SolverMode = SchedulingSolverMode.Csp };

// Outcome và số liệu CSP do chính service ghi lại trong lần chạy đó, không suy đoán từ một lần giải riêng.
using var outcomes = new OutcomeRecorder();

Console.WriteLine($"Running {scenarios.Count} scenarios × 3 modes × (1 warmup + {iterations} measured)...");
var scenarioRows = new List<SolverComparisonScenarioRow>(scenarios.Count);
var timingRows = new List<SolverComparisonTimingRow>(scenarios.Count * 3 * iterations);
foreach (var scenario in scenarios)
{
    var disabled = await Measure(scenario, disabledOptions, SolverComparisonReport.DisabledMode, iterations, outcomes, timingRows);
    var heuristic = await Measure(scenario, heuristicOptions, SolverComparisonReport.HeuristicMode, iterations, outcomes, timingRows);
    var csp = await Measure(scenario, cspOptions, SolverComparisonReport.CspMode, iterations, outcomes, timingRows);
    var comparison = heuristic.Plan is not null && csp.Plan is not null
        ? Math.Sign(ProductRulePlanComparison.Compare(scenario.Input, scenario.Matrix, csp.Plan, heuristic.Plan))
        : (int?)null;

    scenarioRows.Add(new SolverComparisonScenarioRow(
        scenario.Segment,
        scenario.Name,
        scenario.Input.RestPreference.ToString(),
        scenario.Input.MandatoryPoiIds.Count,
        Verdict(comparison),
        VisitCount(heuristic.Plan),
        VisitCount(csp.Plan),
        OptionalCount(scenario, heuristic.Plan),
        OptionalCount(scenario, csp.Plan),
        Travel(scenario, heuristic.Plan),
        Travel(scenario, csp.Plan),
        heuristic.Plan?.TotalDurationMinutes,
        csp.Plan?.TotalDurationMinutes,
        disabled.Outcome.Name,
        heuristic.Outcome.Name,
        csp.Outcome.Name,
        csp.Outcome.Tag<int>("csp.nodes_expanded"),
        csp.Outcome.Tag<int>("csp.solutions_found"),
        csp.Outcome.Tag<bool>("csp.search_completed"),
        csp.Outcome.Tag<bool>("csp.node_limit_reached"),
        csp.Outcome.Tag<bool>("csp.time_limit_reached")));
}

var metadata = new SolverComparisonMetadata(
    Date: DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
    SourceHash: source.Hash,
    SourceFileCount: source.FileCount,
    GeneratedAtUtc: DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture),
    OperatingSystem: RuntimeInformation.OSDescription,
    Processor: Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER") ?? RuntimeInformation.ProcessArchitecture.ToString(),
    LogicalProcessors: Environment.ProcessorCount,
    Runtime: RuntimeInformation.FrameworkDescription,
#if DEBUG
    BuildConfiguration: "Debug",
#else
    BuildConfiguration: "Release",
#endif
    WarmupIterations: 1,
    MeasuredIterations: iterations,
    Seeds: seeds,
    CandidateCounts: candidateCounts,
    MandatoryCounts: mandatoryCounts,
    RestPreferences: restPreferences.Select(preference => preference.ToString()).ToArray(),
    NamedScenarioCount: namedScenarioCount,
    MaxMatrixCandidates: cspOptions.MaxMatrixCandidates,
    Csp: cspOptions.Csp);

SolverComparisonReport.Write(outputDirectory, new SolverComparisonData(metadata, scenarioRows, timingRows));
var baseName = SolverComparisonReport.BaseName(metadata.Date);
Console.WriteLine($"Wrote {Path.Combine(outputDirectory, baseName)}.{{md,csv,timings.csv,meta.json}}");
if (SolverComparisonReport.ComputeSourceHash(repositoryRoot).Hash != source.Hash)
{
    Console.Error.WriteLine("The benchmarked source changed while the benchmark was running; run it again.");
    return 1;
}

return 0;

static Scenario Named(string name, (GenerationInput Input, RouteDurationMatrix Matrix) fixture) =>
    new("named", name, fixture.Input, fixture.Matrix);

static async Task<ModeResult> Measure(
    Scenario scenario,
    SchedulingGenerationOptions options,
    string mode,
    int iterations,
    OutcomeRecorder outcomes,
    List<SolverComparisonTimingRow> timings)
{
    var service = new ItineraryGenerationService(new FixedMatrixProvider(scenario.Matrix), options: options);

    outcomes.Reset();
    var warmup = await service.GenerateAsync(scenario.Input, CancellationToken.None);
    var warmupOutcome = outcomes.Take();

    for (var i = 1; i <= iterations; i++)
    {
        outcomes.Reset();
        var watch = Stopwatch.StartNew();
        _ = await service.GenerateAsync(scenario.Input, CancellationToken.None);
        watch.Stop();
        timings.Add(new SolverComparisonTimingRow(
            scenario.Segment, scenario.Name, mode, i, watch.Elapsed.TotalMilliseconds, outcomes.Take().Name));
    }

    return new ModeResult(warmup.IsSuccess ? warmup.Value : null, warmupOutcome);
}

static int? VisitCount(GeneratedItineraryPlan? plan) =>
    plan?.Items.Count(item => item.Kind == ItineraryItemKind.Visit);

static int? OptionalCount(Scenario scenario, GeneratedItineraryPlan? plan) =>
    plan?.Items.Count(item => item.Kind == ItineraryItemKind.Visit
        && item.PointOfInterestId is { } id
        && !scenario.Input.MandatoryPoiIds.Contains(id));

static int? Travel(Scenario scenario, GeneratedItineraryPlan? plan) =>
    plan is null ? null : ProductRulePlanComparison.MatrixTravelMinutes(scenario.Input, scenario.Matrix, plan);

static string Verdict(int? comparison) => comparison switch
{
    < 0 => "better",
    0 => "equal",
    > 0 => "worse",
    null => "excluded",
};

static string? FindRepositoryRoot(string start)
{
    for (var directory = new DirectoryInfo(start); directory is not null; directory = directory.Parent)
    {
        if (File.Exists(Path.Combine(directory.FullName, "TripMate.slnx")))
        {
            return directory.FullName;
        }
    }

    return null;
}

internal sealed record Scenario(string Segment, string Name, GenerationInput Input, RouteDurationMatrix Matrix);

internal sealed record ModeResult(GeneratedItineraryPlan? Plan, RecordedOutcome Outcome);

internal sealed record RecordedOutcome(string Name, IReadOnlyDictionary<string, object?> Tags)
{
    /// <summary>Lần sinh trả lỗi trước khi tới bước chọn bộ giải (ví dụ yêu cầu không hợp lệ) không ghi outcome.</summary>
    public static readonly RecordedOutcome None = new("none", new Dictionary<string, object?>());

    public T? Tag<T>(string name)
        where T : struct =>
        Tags.TryGetValue(name, out var value) && value is T typed ? typed : null;
}

/// <summary>Bắt activity <c>SchedulingSolverOutcome</c> của lần sinh vừa chạy (runner chạy tuần tự).</summary>
internal sealed class OutcomeRecorder : IDisposable
{
    private readonly ActivityListener _listener;
    private RecordedOutcome? _last;

    public OutcomeRecorder()
    {
        _listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == SchedulingSolverDiagnostics.ActivitySourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity =>
            {
                if (activity.OperationName == SchedulingSolverDiagnostics.OutcomeActivityName)
                {
                    _last = new RecordedOutcome(
                        activity.GetTagItem("solver.outcome") as string ?? "none",
                        activity.TagObjects.ToDictionary(tag => tag.Key, tag => tag.Value));
                }
            },
        };
        ActivitySource.AddActivityListener(_listener);
    }

    public void Reset() => _last = null;

    public RecordedOutcome Take() => _last ?? RecordedOutcome.None;

    public void Dispose() => _listener.Dispose();
}

internal sealed class FixedMatrixProvider(RouteDurationMatrix matrix) : IRouteDurationProvider
{
    public Task<RouteDurationMatrix> GetMatrixAsync(
        IReadOnlyList<RoutePoint> points,
        TransportMode transportMode,
        CancellationToken cancellationToken) => Task.FromResult(matrix);
}