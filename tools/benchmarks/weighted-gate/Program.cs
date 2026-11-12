// ADR-002 (G4b): measures the WeightedUtility switch gate on the shipped configuration.
// Usage (from the repository root):
//   dotnet run -c Release --project tools/benchmarks/weighted-gate -- [--label name] [--samples 3] [--warmup 1] [--set "Key=Value;Key2=Value2"]
// Writes docs/benchmarks/UC-10-weighted-gate-<yyyy-MM-dd>[-<label>].md and .csv, and exits 1 when the gate fails. --set runs a
// calibration experiment on top of appsettings.json; the report then says it is not the shipped configuration.
using System.Globalization;

using TripMate.Application.UnitTests.Features.Scheduling.Benchmarks.Shadow;
using TripMate.Application.UnitTests.Features.Scheduling.Benchmarks.Weighted;
using TripMate.Application.UnitTests.Features.Scheduling.Fixtures;
using TripMate.Application.UnitTests.Features.Scheduling.Golden;
using TripMate.Domain.Enums;

var arguments = ParseArguments(args);
var samples = int.Parse(arguments.GetValueOrDefault("samples", "3"), CultureInfo.InvariantCulture);
var warmup = int.Parse(arguments.GetValueOrDefault("warmup", "1"), CultureInfo.InvariantCulture);
var label = arguments.GetValueOrDefault("label");
if (label is not null && !label.All(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c)))
{
    Console.Error.WriteLine("--label must be lowercase ASCII letters and digits.");
    return 2;
}

// --set "Key=Value;Key2=Value2" overrides the shipped configuration for calibration experiments.
var overrides = arguments.GetValueOrDefault("set")?
    .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
    .Select(pair => pair.Split('=', 2))
    .ToDictionary(pair => pair[0], pair => (string?)pair[1]);
var root = WeightedGateConfiguration.FindRepositoryRoot(Directory.GetCurrentDirectory());
// --load "1,4,8" [--rounds 2] [--cpus 2]: local load test (ADR-002 §8) instead of the gate. Start the process with
// DOTNET_gcServer=1 (ASP.NET default) and DOTNET_PROCESSOR_COUNT matching --cpus so the thread pool sees the same limit.
if (arguments.TryGetValue("load", out var load))
{
    if (arguments.TryGetValue("cpus", out var cpus))
    {
        System.Diagnostics.Process.GetCurrentProcess().ProcessorAffinity =
            (nint)((1L << int.Parse(cpus, CultureInfo.InvariantCulture)) - 1);
    }

    var loadDate = DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    var report = await LoadTest.RunAsync(
        root,
        overrides,
        load.Split(',').Select(level => int.Parse(level, CultureInfo.InvariantCulture)).ToArray(),
        int.Parse(arguments.GetValueOrDefault("rounds", "2"), CultureInfo.InvariantCulture),
        loadDate);
    var loadOutput = Path.Combine(root, "docs", "benchmarks", $"UC-10-weighted-load-{loadDate}{(label is null ? string.Empty : "-" + label)}.md");
    await File.WriteAllTextAsync(loadOutput, report);
    Console.WriteLine(loadOutput);
    return 0;
}

var configuration = WeightedGateConfiguration.LoadShipped(root, overrides);
var cityData = SchedulingBenchmarkScenarioFactory.CreateDaNangV3bData();
var personas = cityData.Personas;

var latencyRuns = new List<WeightedGateRun>();
foreach (var scenario in GoldenScenarioFactory.CreateAllScenarios().Where(scenario => scenario.Id.StartsWith("grid-c", StringComparison.Ordinal)))
{
    foreach (var persona in personas)
    {
        latencyRuns.Add(await WeightedGateExecutor.RunAsync(scenario, persona, configuration, warmup, samples));
    }
}

var cityScenario = SchedulingBenchmarkScenarioFactory.CreateScenario(cityData);
var paceRuns = new List<WeightedGateRun>();
foreach (var persona in personas.Where(persona => SchedulingBenchmarkScenarioFactory.CreateProfile(persona, cityData.StartAtUtc) is not null))
{
    foreach (var pace in new[] { TravelerPace.Relaxed, TravelerPace.Moderate, TravelerPace.Fast })
    {
        paceRuns.Add(await WeightedGateExecutor.RunAsync(cityScenario, persona with { Pace = pace }, configuration, warmup, samples));
    }
}

// --reference-nodes N (G5): solve every latency request once more with N nodes and no practical time limit, outside the timed
// runs, and report how much J the shipped node budget leaves on the table.
int? referenceNodes = arguments.TryGetValue("reference-nodes", out var referenceText)
    ? int.Parse(referenceText, CultureInfo.InvariantCulture)
    : null;
if (referenceNodes is { } nodes)
{
    var reference = WeightedGateConfiguration.LoadShipped(
        root,
        new Dictionary<string, string?>(overrides ?? new Dictionary<string, string?>())
        {
            ["SchedulingGeneration:Csp:WeightedMaxNodes"] = nodes.ToString(CultureInfo.InvariantCulture),
            ["SchedulingGeneration:Csp:TimeLimitMilliseconds"] = "600000",
        });
    var scenarios = GoldenScenarioFactory.CreateAllScenarios().ToDictionary(scenario => scenario.Id);
    for (var index = 0; index < latencyRuns.Count; index++)
    {
        var run = latencyRuns[index];
        var best = await WeightedGateExecutor.RunAsync(
            scenarios[run.ScenarioId], personas.Single(persona => persona.Id == run.PersonaId), reference, 0, 1);
        latencyRuns[index] = run with { ReferenceObjective = best.ModelObjective };
    }
}

var summary = WeightedGateSummary.Create(latencyRuns, paceRuns);
var runDate = DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
var markdown = WeightedGateMarkdownRenderer.Render(
    summary,
    configuration,
    paceRuns,
    runDate,
    Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER") ?? Environment.MachineName,
    Environment.ProcessorCount,
    warmup,
    samples,
    referenceNodes);
var output = Path.Combine(root, "docs", "benchmarks", $"UC-10-weighted-gate-{runDate}{(label is null ? string.Empty : "-" + label)}.md");
await File.WriteAllTextAsync(output, markdown);
await File.WriteAllLinesAsync(
    Path.ChangeExtension(output, ".csv"),
    new[] { "group,scenario,candidates,persona,pace,feasible,stops,model_objective,heuristic_objective,greedy_objective,outcome,search_completed,node_limit,time_limit,nodes,evaluator_calls,csp_ms,p95_ms,visits,categories,reference_objective" }
        .Concat(latencyRuns.Select(run => Row("latency", run)))
        .Concat(paceRuns.Select(run => Row("pace", run))));
Console.WriteLine(output);
Console.WriteLine(summary.Passed ? "Gate PASSED" : "Gate FAILED");
return summary.Passed ? 0 : 1;

static string Row(string group, WeightedGateRun run) => string.Join(',',
    group,
    run.ScenarioId,
    run.CandidateCount.ToString(CultureInfo.InvariantCulture),
    run.PersonaId,
    run.Pace,
    run.Feasible,
    run.StopCount.ToString(CultureInfo.InvariantCulture),
    run.ModelObjective?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
    run.HeuristicObjective?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
    run.GreedyObjective?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
    run.Outcome ?? string.Empty,
    run.SearchCompleted?.ToString() ?? string.Empty,
    run.NodeLimitReached,
    run.TimeLimitReached,
    run.NodesExpanded.ToString(CultureInfo.InvariantCulture),
    run.EvaluatorCalls.ToString(CultureInfo.InvariantCulture),
    run.CspElapsedMs.ToString("F1", CultureInfo.InvariantCulture),
    SolverComparisonReport.Percentile(run.SamplesMs, 0.95).ToString("F1", CultureInfo.InvariantCulture),
    string.Join(";", run.VisitPoiIds),
    string.Join(";", run.VisitCategories).Replace(",", " ", StringComparison.Ordinal),
    run.ReferenceObjective?.ToString(CultureInfo.InvariantCulture) ?? string.Empty);

static Dictionary<string, string> ParseArguments(string[] values)
{
    var parsed = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    for (var index = 0; index < values.Length; index += 2)
    {
        if (index + 1 >= values.Length || !values[index].StartsWith("--", StringComparison.Ordinal))
        {
            throw new ArgumentException("Arguments must be '--name value' pairs.");
        }

        parsed[values[index][2..]] = values[index + 1];
    }

    return parsed;
}
