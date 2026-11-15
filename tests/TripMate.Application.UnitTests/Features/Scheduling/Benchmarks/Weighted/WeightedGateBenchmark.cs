using System.Diagnostics;
using System.Globalization;
using System.Text;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;

using TripMate.Application.Common.Geo;
using TripMate.Application.Common.Interfaces;
using TripMate.Application.Features.Scheduling.Common;
using TripMate.Application.Features.Scheduling.Csp;
using TripMate.Application.Features.Scheduling.Csp.Objectives;
using TripMate.Application.Features.Scheduling.Personalization;
using TripMate.Application.Features.Scheduling.Routing;
using TripMate.Application.Features.Scheduling.TimeOfDay;
using TripMate.Application.UnitTests.Features.Scheduling.Benchmarks.Shadow;
using TripMate.Application.UnitTests.Features.Scheduling.Golden;
using TripMate.Domain.Enums;

namespace TripMate.Application.UnitTests.Features.Scheduling.Benchmarks.Weighted;

/// <summary>Shipped configuration with only <c>SchedulingGeneration:Csp:Mode</c> switched to WeightedUtility.</summary>
public sealed record WeightedGateConfiguration(
    SchedulingGenerationOptions Generation,
    PersonalizationOptions Personalization,
    PersonalizationRankingOptions Ranking,
    TimeFitOptions TimeFit)
{
    /// <summary>The <c>--set</c> keys applied on top of <c>appsettings.json</c>; empty for a shipped-configuration run.</summary>
    public IReadOnlyDictionary<string, string?> Overrides { get; init; } = new Dictionary<string, string?>();

    /// <param name="overrides">Calibration experiments only (for example <c>SchedulingGeneration:Csp:WeightedMaxNodes</c>).</param>
    public static WeightedGateConfiguration LoadShipped(
        string repositoryRoot,
        IReadOnlyDictionary<string, string?>? overrides = null)
    {
        var configuration = new ConfigurationBuilder()
            .AddJsonFile(Path.Combine(repositoryRoot, "src", "TripMate.Api", "appsettings.json"), optional: false)
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["SchedulingGeneration:Csp:Mode"] = nameof(ObjectiveMode.WeightedUtility),
            })
            .AddInMemoryCollection(overrides ?? new Dictionary<string, string?>())
            .Build();
        return new WeightedGateConfiguration(
            configuration.GetSection(SchedulingGenerationOptions.SectionName).Get<SchedulingGenerationOptions>()!,
            configuration.GetSection(PersonalizationOptions.SectionName).Get<PersonalizationOptions>()!,
            configuration.GetSection(PersonalizationRankingOptions.SectionName).Get<PersonalizationRankingOptions>()!,
            configuration.GetSection(TimeFitOptions.SectionName).Get<TimeFitOptions>()!)
        {
            Overrides = overrides ?? new Dictionary<string, string?>(),
        };
    }

    public static string FindRepositoryRoot(string start)
    {
        for (var directory = new DirectoryInfo(start); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "TripMate.slnx")))
            {
                return directory.FullName;
            }
        }

        throw new DirectoryNotFoundException("TripMate.slnx was not found above " + start);
    }
}

/// <summary>
/// ADR-002 (G4b): runs WeightedUtility through <see cref="ItineraryGenerationService"/> with the shipped limits (2.5 s, node
/// budget) and reads the solver outcome from the same diagnostics activity production logs.
/// </summary>
public static class WeightedGateExecutor
{
    /// <summary>The behavior affinity production uses when a traveler has no evidence for a POI or its category.</summary>
    internal const decimal NoBehaviorAffinity = 0.5m;

    /// <summary>Production interest-token normalization, for tools outside the test assembly.</summary>
    public static string NormalizeInterest(string value) => TravelerPreferenceScoring.NormalizePreferenceToken(value);

    public static async Task<WeightedGateRun> RunAsync(
        GoldenScenario scenario,
        BenchmarkPersonaData persona,
        WeightedGateConfiguration configuration,
        int warmupIterations,
        int measuredIterations,
        CancellationToken cancellationToken = default,
        IReadOnlyDictionary<long, decimal>? aiScores = null,
        IReadOnlyDictionary<long, decimal>? behaviorAffinities = null,
        IReadOnlySet<long>? excludedPoiIds = null,
        int? matrixCandidateCap = null)
    {
        var baseScorer = new PersonalizationBaseScorer(configuration.Ranking);
        var personalized = scenario.Candidates
            .Select(candidate => Personalize(
                candidate,
                persona,
                baseScorer,
                configuration.Ranking,
                aiScores is not null && aiScores.TryGetValue(candidate.Id, out var aiScore) ? aiScore : null,
                behaviorAffinities is not null && behaviorAffinities.TryGetValue(candidate.Id, out var affinity)
                    ? affinity
                    : NoBehaviorAffinity))
            .ToArray();

        // Production drops two kinds of optional candidates before routing: excluded ones (the removed-stop
        // cooldown) and, in a large city, the ones below the matrix cap (the solver only gets the best-ranked).
        var dropped = new HashSet<long>(excludedPoiIds ?? new HashSet<long>());
        if (matrixCandidateCap is { } cap)
        {
            var mandatory = scenario.Input.MandatoryPoiIds;
            var kept = MatrixCandidateSelector.Select(
                    mandatory,
                    personalized
                        .Where(candidate => !mandatory.Contains(candidate.Id) && !dropped.Contains(candidate.Id))
                        .Select(candidate => new PoiRankingSnapshotEntry(
                            candidate.Id,
                            candidate.TripMateBaseScore ?? 0m,
                            candidate.EffectiveDesirabilityScore ?? 0m,
                            candidate.ScenicScore,
                            candidate.PhotoRating,
                            GeoDistance.EquirectangularKilometers(
                                scenario.Input.Start.Latitude,
                                scenario.Input.Start.Longitude,
                                candidate.Location.Latitude,
                                candidate.Location.Longitude),
                            candidate.EstimatedVisitCost))
                        .ToArray(),
                    cap)
                .OrderedPoiIds
                .ToHashSet();
            dropped.UnionWith(personalized.Select(candidate => candidate.Id).Where(id => !kept.Contains(id)));
        }

        // The scenario's route matrix is indexed by candidate position, so a dropped candidate stays in place
        // with a visit that cannot fit the day instead of being removed.
        var candidates = personalized
            .Select(candidate => dropped.Contains(candidate.Id)
                ? candidate with { VisitDurationMinutes = scenario.Input.AvailableMinutes + 1 }
                : candidate)
            .ToArray();
        var input = scenario.Input with { Candidates = candidates };
        var weighted = new WeightedRequestInputsFactory(
                new PersonalParamsFactory(Options.Create(configuration.Personalization)),
                timeFitOptions: Options.Create(configuration.TimeFit))
            .Create(
                candidates,
                SchedulingBenchmarkScenarioFactory.CreateProfile(persona, scenario.Input.StartAtUtc),
                input,
                PoiRankingOutcomeCategory.ProviderSkipped);

        for (var iteration = 0; iteration < warmupIterations; iteration++)
        {
            _ = await ExecuteAsync(scenario, input, weighted, configuration.Generation, cancellationToken);
        }

        var samples = new double[measuredIterations];
        Execution last = default!;
        for (var iteration = 0; iteration < measuredIterations; iteration++)
        {
            last = await ExecuteAsync(scenario, input, weighted, configuration.Generation, cancellationToken);
            samples[iteration] = last.ElapsedMilliseconds;
        }

        var visits = last.Plan?.Items.Where(item => item.Kind == ItineraryItemKind.Visit && item.PointOfInterestId.HasValue).ToArray() ?? [];
        return new WeightedGateRun(
            scenario.Id,
            scenario.Candidates.Count,
            persona.Id,
            PersonalParamsFactory.EffectivePace(
                SchedulingBenchmarkScenarioFactory.CreateProfile(persona, scenario.Input.StartAtUtc)),
            last.Plan is not null,
            last.Plan?.Items.Count(item => item.Kind == ItineraryItemKind.Visit) ?? 0,
            last.Plan?.ModelObjective,
            Objective(input, scenario, weighted, configuration.Generation, last.Optimization?.BaselineSchedule),
            GreedyObjective(input, scenario, weighted, configuration.Generation, cancellationToken),
            last.Tags.GetValueOrDefault("solver.outcome") as string,
            last.Tags.GetValueOrDefault("csp.search_completed") as bool?,
            last.Tags.GetValueOrDefault("csp.node_limit_reached") as bool? ?? false,
            last.Tags.GetValueOrDefault("csp.time_limit_reached") as bool? ?? false,
            last.Tags.GetValueOrDefault("csp.nodes_expanded") as int? ?? 0,
            last.Tags.GetValueOrDefault("csp.evaluator_calls") as int? ?? 0,
            last.Tags.GetValueOrDefault("csp.elapsed_ms") as double? ?? 0,
            samples)
        {
            VisitPoiIds = visits.Select(item => item.PointOfInterestId!.Value).ToArray(),
            OptionalVisitPoiIds = visits.Where(item => !item.IsMandatory).Select(item => item.PointOfInterestId!.Value).ToArray(),
            VisitCategories = visits.Select(item => candidates.First(candidate => candidate.Id == item.PointOfInterestId).CategoryName ?? "?").ToArray(),
            PlanMinutes = last.Plan?.TotalDurationMinutes ?? 0,
            AvailableMinutes = input.AvailableMinutes,
        };
    }

    /// <summary>
    /// J of an evaluated schedule in the context <see cref="ItineraryGenerationService"/> uses for the served plan's
    /// <c>ModelObjective</c>, so the three objectives of a run are on one scale (ADR-002 §4.4).
    /// </summary>
    private static long? Objective(
        GenerationInput input,
        GoldenScenario scenario,
        WeightedRequestInputs weighted,
        SchedulingGenerationOptions options,
        EvaluatedItinerarySchedule? schedule)
    {
        if (schedule is null)
        {
            return null;
        }

        var (ctx, objectiveInputs, _, _) = Context(input, scenario, weighted, options);
        return ObjectiveCalculator.Evaluate(objectiveInputs, ctx, schedule);
    }

    /// <summary>The greedy weighted plan over every candidate, evaluated like the solver's greedy incumbent; outside the timed runs.</summary>
    private static long? GreedyObjective(
        GenerationInput input,
        GoldenScenario scenario,
        WeightedRequestInputs weighted,
        SchedulingGenerationOptions options,
        CancellationToken cancellationToken)
    {
        var (ctx, objectiveInputs, matrixCandidates, indices) = Context(input, scenario, weighted, options);
        var evaluator = new ItineraryScheduleEvaluator(options);
        var greedy = GreedyWeightedIncumbent.Build(
            ctx,
            objectiveInputs,
            options.Csp,
            (context, nodes, _) => evaluator.Evaluate(
                input,
                nodes.Select(node => context.Nodes[node].Candidate).ToArray(),
                scenario.Matrix,
                matrixCandidates,
                indices,
                cancellationToken));
        return greedy is null ? null : ObjectiveCalculator.Evaluate(objectiveInputs, ctx, greedy);
    }

    private static (RoutingContext Ctx, WeightedObjectiveInputs Inputs, GenerationCandidate[] MatrixCandidates, Dictionary<long, int> Indices) Context(
        GenerationInput input,
        GoldenScenario scenario,
        WeightedRequestInputs weighted,
        SchedulingGenerationOptions options)
    {
        var matrixCandidates = input.Candidates.ToArray();
        var indices = matrixCandidates.Select((candidate, index) => (candidate.Id, index + 1)).ToDictionary(pair => pair.Id, pair => pair.Item2);
        var ctx = RoutingContext.Create(input, scenario.Matrix, matrixCandidates, indices, options, options.MiniRouting);
        return (ctx, ObjectiveKernelFactory.BuildWeightedInputs(ctx, weighted), matrixCandidates, indices);
    }

    /// <summary>Production ranking: base score blended with the AI fallback score, because AI ranking ships disabled.</summary>
    /// <param name="aiScore">The provider AI score for this POI; null falls back like production (no AI answer).</param>
    /// <param name="behaviorAffinity">The count-based behavior affinity; 0.5 is production's "no evidence".</param>
    internal static GenerationCandidate Personalize(
        GenerationCandidate candidate,
        BenchmarkPersonaData persona,
        PersonalizationBaseScorer baseScorer,
        PersonalizationRankingOptions ranking,
        decimal? aiScore,
        decimal behaviorAffinity = NoBehaviorAffinity)
    {
        var category = candidate.CategoryName ?? "Culture";
        var features = PersonalizationFeatureBuilder.Build(
            persona.Tags.Select(TravelerPreferenceScoring.NormalizePreferenceToken).ToArray(),
            category,
            candidate.Tags ?? [],
            candidate.ScenicScore,
            candidate.PhotoRating);
        var baseScore = baseScorer.Score(features, behaviorAffinity);
        var ai = aiScore ?? (persona.Tags.Count > 0 ? ranking.PersonalizedAiFallbackScore : ranking.DefaultAiFallbackScore);
        return candidate with
        {
            CategoryName = category,
            TripMateBaseScore = baseScore,
            EffectiveDesirabilityScore = (ranking.BaseWeight * baseScore) + (ranking.AiWeight * ai),
        };
    }

    private static async Task<Execution> ExecuteAsync(
        GoldenScenario scenario,
        GenerationInput input,
        WeightedRequestInputs weighted,
        SchedulingGenerationOptions options,
        CancellationToken cancellationToken)
    {
        using var parent = new Activity(nameof(WeightedGateExecutor)).Start();
        var traceId = parent.TraceId;
        var tags = new Dictionary<string, object?>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == SchedulingSolverDiagnostics.ActivitySourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> creation) => creation.TraceId == traceId
                ? ActivitySamplingResult.AllDataAndRecorded
                : ActivitySamplingResult.None,
            ActivityStopped = activity =>
            {
                if (activity.TraceId == traceId && activity.OperationName == SchedulingSolverDiagnostics.OutcomeActivityName)
                {
                    foreach (var (key, value) in activity.TagObjects)
                    {
                        tags[key] = value;
                    }
                }
            },
        };
        ActivitySource.AddActivityListener(listener);

        OptimizationResult? optimization = null;
        var service = new ItineraryGenerationService(new StaticMatrixProvider(scenario.Matrix), options)
        {
            OnOptimizationCompleted = result => optimization = result,
        };
        var stopwatch = Stopwatch.StartNew();
        var result = await service.GenerateAsync(input, weighted, cancellationToken);
        stopwatch.Stop();
        return new Execution(result.IsSuccess ? result.Value : null, tags, stopwatch.Elapsed.TotalMilliseconds, optimization);
    }

    private sealed record Execution(
        GeneratedItineraryPlan? Plan,
        Dictionary<string, object?> Tags,
        double ElapsedMilliseconds,
        OptimizationResult? Optimization);

    private sealed class StaticMatrixProvider(RouteDurationMatrix matrix) : IRouteDurationProvider
    {
        public Task<RouteDurationMatrix> GetMatrixAsync(
            IReadOnlyList<RoutePoint> points,
            TransportMode transportMode,
            CancellationToken cancellationToken) => Task.FromResult(matrix);
    }
}

public static class WeightedGateMarkdownRenderer
{
    public static string Render(
        WeightedGateSummary summary,
        WeightedGateConfiguration configuration,
        IReadOnlyList<WeightedGateRun> paceRuns,
        string runDate,
        string machine,
        int logicalProcessors,
        int warmupIterations,
        int measuredIterations,
        int? referenceNodes = null)
    {
        var text = new StringBuilder();
        var invariant = CultureInfo.InvariantCulture;
        static string Pass(bool value) => value ? "passed" : "**failed**";

        text.AppendLine("# UC-10 WeightedUtility switch gate (ADR-002 G4b)");
        text.AppendLine();
        text.AppendLine("> Generated by `tools/benchmarks/weighted-gate`; do not edit by hand.");
        text.AppendLine();
        text.AppendLine("## Environment and configuration");
        text.AppendLine();
        text.AppendLine(invariant, $"- Run date: {runDate}; machine: {machine}; logical processors: {logicalProcessors}; build: Release.");
        text.AppendLine(invariant, $"- Iterations: {warmupIterations} warmup + {measuredIterations} measured per run, sequential, one process.");
        var csp = configuration.Generation.Csp;
        text.AppendLine(invariant, $"- Shipped `appsettings.json` with `Csp:Mode=WeightedUtility`: time limit {csp.TimeLimitMilliseconds} ms, node budget {csp.WeightedMaxNodes ?? csp.MaxNodes}, personalization `{configuration.Personalization.Version}`, AI ranking off (fallback scores).");
        foreach (var (pace, parameters) in configuration.Personalization.Pace.OrderBy(pair => pair.Key))
        {
            text.AppendLine(invariant, $"- {pace}: λ {parameters.LambdaPerMin}, max stops/day {parameters.MaxStopsPerDay}, max daily effort {parameters.MaxDailyEffort}, rest after {parameters.RestAfterMin} min.");
        }

        text.AppendLine(invariant, $"- RhoStops {configuration.Personalization.RhoStops}, RhoOutdoor {configuration.Personalization.RhoOutdoor}, Mu {configuration.Personalization.Mu}, γ {configuration.Personalization.GammaDefault}/{configuration.Personalization.GammaLoved}.");
        if (configuration.Overrides.Count > 0)
        {
            text.AppendLine(invariant, $"- **Experiment, not the shipped configuration.** Overrides: {string.Join("; ", configuration.Overrides.Select(pair => $"`{pair.Key}={pair.Value}`"))}.");
        }

        text.AppendLine();
        text.AppendLine("## Latency and limits (corpus c10–c40, every persona)");
        text.AppendLine();
        text.AppendLine("| Candidates | Runs | p50 ms | p95 ms | p95 limit | p99 ms | max ms | Time limit | Node limit | Search completed |");
        text.AppendLine("|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|");
        foreach (var size in summary.LatencyBySize)
        {
            text.AppendLine(invariant, $"| {size.CandidateCount} | {size.Runs} | {size.P50Milliseconds:F1} | {size.P95Milliseconds:F1} | {WeightedGateSummary.P95LimitMilliseconds(size.CandidateCount):F0} | {size.P99Milliseconds:F1} | {size.MaxMilliseconds:F1} | {size.TimeLimitRuns} | {size.NodeLimitRuns} | {size.SearchCompletedRuns} |");
        }

        text.AppendLine();
        text.AppendLine(invariant, $"Overall: p95 {summary.P95Milliseconds:F1} ms, p99 {summary.P99Milliseconds:F1} ms, max {summary.MaxMilliseconds:F1} ms.");
        text.AppendLine();
        text.AppendLine("## Stops per pace (8-hour trips)");
        text.AppendLine();
        text.AppendLine("| Pace | Runs | Infeasible | Mean stops | Min | Max | Target |");
        text.AppendLine("|---|---:|---:|---:|---:|---:|---|");
        foreach (var pace in summary.StopsByPace)
        {
            text.AppendLine(invariant, $"| {pace.Pace} | {pace.Runs} | {pace.InfeasibleRuns} | {pace.MeanStops:F2} | {pace.MinStops} | {pace.MaxStops} | {pace.TargetMin}–{pace.TargetMax} {(pace.WithinTarget ? "✅" : "❌")} |");
        }

        text.AppendLine();
        text.AppendLine("| Scenario | Persona | Pace | Stops | Plan minutes | Outcome |");
        text.AppendLine("|---|---|---|---:|---:|---|");
        foreach (var run in paceRuns.OrderBy(run => run.ScenarioId).ThenBy(run => run.PersonaId).ThenBy(run => run.Pace))
        {
            text.AppendLine(invariant, $"| {run.ScenarioId} | {run.PersonaId} | {run.Pace} | {(run.Feasible ? run.StopCount.ToString(invariant) : "infeasible")} | {run.PlanMinutes}/{run.AvailableMinutes} | {run.Outcome} |");
        }

        text.AppendLine();
        text.AppendLine("## Persona overlap (optional stops, pairwise Jaccard)");
        text.AppendLine();
        text.AppendLine("| Pace | Pairs | Mean | Max |");
        text.AppendLine("|---|---:|---:|---:|");
        foreach (var overlap in summary.OverlapByPace)
        {
            text.AppendLine(invariant, $"| {overlap.Pace} | {overlap.Pairs} | {overlap.MeanJaccard:F2} | {overlap.MaxJaccard:F2} |");
        }

        text.AppendLine();
        if (summary.ReferenceGapBySize.Count > 0)
        {
            text.AppendLine(invariant, $"## Loss against a larger search (G5; reference node budget {referenceNodes:N0})");
            text.AppendLine();
            text.AppendLine("The root upper bound of the CSP is too loose to report a useful optimality gap, so the shipped plan is compared");
            text.AppendLine("with the same request solved under a much larger node budget. Loss is (reference J − J) / |reference J|.");
            text.AppendLine();
            text.AppendLine("| Candidates | Runs | Below reference | Mean loss % | Max loss % |");
            text.AppendLine("|---:|---:|---:|---:|---:|");
            foreach (var size in summary.ReferenceGapBySize)
            {
                text.AppendLine(invariant, $"| {size.CandidateCount} | {size.Runs} | {size.BelowReference} | {size.MeanLossPercent:F1} | {size.MaxLossPercent:F1} |");
            }

            text.AppendLine();
        }

        text.AppendLine("## Gate status");
        text.AppendLine();
        text.AppendLine(invariant, $"- p95 ≤ 300 ms below 40 candidates and ≤ 800 ms at 40 (ADR-002 §4, amended 2026-11-11): {Pass(summary.LatencyPassed)}");
        text.AppendLine(invariant, $"- No time-limit fallback: {Pass(summary.NoTimeLimit)} ({summary.TimeLimitRuns} runs)");
        text.AppendLine(invariant, $"- Stops within the D2 targets: {Pass(summary.PaceTargetsPassed)}");
        text.AppendLine(invariant, $"- Stops never decrease from Relaxed to Fast: {Pass(summary.PaceOrderPassed)}");
        text.AppendLine(invariant, $"- By J, the served plan is not worse than the heuristic plan or the greedy incumbent: {Pass(summary.NotWorseThanBaselines)} ({summary.DominanceViolations} of {summary.DominanceRuns} runs worse)");
        text.AppendLine(invariant, $"- Personas share less than {WeightedGateSummary.MaxMeanJaccard:F1} of optional stops on average at every pace: {Pass(summary.PersonasDistinct)}");
        text.AppendLine(invariant, $"- **Overall: {(summary.Passed ? "PASSED" : "FAILED")}**");
        text.AppendLine();
        text.AppendLine("## Limitations");
        text.AppendLine();
        text.AppendLine("- Generator only: no Mapbox, ranking or database latency; matrices come from the fixtures.");
        text.AppendLine("- One machine, sequential runs; concurrency and CPU limits are measured by the separate load test.");
        text.AppendLine("- Personas stand in for real travelers; behavior history is neutral (no likes or dislikes).");
        return text.ToString();
    }
}