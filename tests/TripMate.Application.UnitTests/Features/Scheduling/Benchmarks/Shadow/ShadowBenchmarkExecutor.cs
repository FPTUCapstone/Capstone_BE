using System.Diagnostics;

using Microsoft.Extensions.Options;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Features.Scheduling.Common;
using TripMate.Application.Features.Scheduling.Csp;
using TripMate.Application.Features.Scheduling.Csp.Objectives;
using TripMate.Application.Features.Scheduling.Personalization;
using TripMate.Application.UnitTests.Features.Scheduling.Golden;
using TripMate.Domain.Constants;
using TripMate.Domain.Enums;

namespace TripMate.Application.UnitTests.Features.Scheduling.Benchmarks.Shadow;

public static class ShadowBenchmarkExecutor
{
    private static readonly string[] SyntheticTourCategories =
    [
        PoiCategoryNames.Culture,
        PoiCategoryNames.Museum,
        PoiCategoryNames.NaturalAttraction,
        PoiCategoryNames.Nature,
        PoiCategoryNames.Beach,
        PoiCategoryNames.ThemePark,
        PoiCategoryNames.WaterPark
    ];

    public static async Task<IReadOnlyList<ShadowBenchmarkRunResult>> RunAsync(
        IReadOnlyList<GoldenScenario> scenarios,
        IReadOnlyList<BenchmarkPersonaData> personas,
        int warmupIterations,
        int measuredIterations,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(warmupIterations);
        ArgumentOutOfRangeException.ThrowIfLessThan(measuredIterations, 1);

        var results = new List<ShadowBenchmarkRunResult>(scenarios.Count * personas.Count * 2);
        foreach (var scenario in scenarios)
        {
            foreach (var persona in personas)
            {
                results.Add(await RunScenarioAsync(
                    scenario, persona, ObjectiveMode.Lexicographic, warmupIterations, measuredIterations, cancellationToken));
                results.Add(await RunScenarioAsync(
                    scenario, persona, ObjectiveMode.WeightedUtility, warmupIterations, measuredIterations, cancellationToken));
            }
        }

        return results;
    }

    public static async Task<ShadowBenchmarkRunResult> RunScenarioAsync(
        GoldenScenario scenario,
        BenchmarkPersonaData persona,
        ObjectiveMode mode,
        int warmupIterations,
        int measuredIterations,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scenario);
        ArgumentNullException.ThrowIfNull(persona);
        ArgumentOutOfRangeException.ThrowIfNegative(warmupIterations);
        ArgumentOutOfRangeException.ThrowIfLessThan(measuredIterations, 1);

        var baseScorer = new PersonalizationBaseScorer(new PersonalizationRankingOptions());
        var candidates = scenario.Candidates.Select(candidate => Personalize(candidate, persona, baseScorer)).ToArray();
        var input = scenario.Input with { Candidates = candidates };
        var weightedFactory = new WeightedRequestInputsFactory(new PersonalParamsFactory(
            Options.Create(SchedulingBenchmarkScenarioFactory.CreatePersonalizationOptions())));
        var weighted = mode == ObjectiveMode.WeightedUtility
            ? weightedFactory.Create(
                candidates,
                SchedulingBenchmarkScenarioFactory.CreateProfile(persona, scenario.Input.StartAtUtc),
                input,
                PoiRankingOutcomeCategory.Success)
            : null;
        var options = OptionsFor(mode);

        for (var iteration = 0; iteration < warmupIterations; iteration++)
        {
            _ = await ExecuteOnceAsync(scenario, input, options, weighted, cancellationToken);
        }

        var samples = new double[measuredIterations];
        EvaluatedItinerarySchedule? schedule = null;
        for (var iteration = 0; iteration < measuredIterations; iteration++)
        {
            var run = await ExecuteOnceAsync(scenario, input, options, weighted, cancellationToken);
            samples[iteration] = run.ElapsedMilliseconds;
            schedule = run.Schedule;
            if (!run.Feasible)
            {
                return new ShadowBenchmarkRunResult(
                    scenario.Id, persona.Id, mode, false, 0, 0, 0, 0, 0, 0, null,
                    [],
                    samples.Take(iteration + 1).ToArray(),
                    ShadowBenchmarkMetrics.Percentile(samples.Take(iteration + 1), 0.50),
                    ShadowBenchmarkMetrics.Percentile(samples.Take(iteration + 1), 0.95));
            }
        }

        var candidateMap = candidates.ToDictionary(candidate => candidate.Id);
        return ShadowBenchmarkMetrics.Create(
            scenario.Id,
            persona.Id,
            mode,
            schedule!,
            candidateMap,
            input.TimeZone,
            samples);
    }

    private static GenerationCandidate Personalize(
        GenerationCandidate candidate,
        BenchmarkPersonaData persona,
        PersonalizationBaseScorer baseScorer)
    {
        var category = string.IsNullOrWhiteSpace(candidate.CategoryName)
            ? SyntheticTourCategories[(int)(Math.Abs(candidate.Id) % SyntheticTourCategories.Length)]
            : candidate.CategoryName;
        var features = PersonalizationFeatureBuilder.Build(
            persona.Tags,
            category,
            candidate.Tags ?? [],
            candidate.ScenicScore ?? 7m,
            candidate.PhotoRating ?? 7m);
        var baseScore = (decimal)Math.Clamp((double)baseScorer.Score(features, 0.5m), 0.05, 0.95);
        return candidate with
        {
            CategoryName = category,
            TripMateBaseScore = baseScore,
            EffectiveDesirabilityScore = baseScore
        };
    }

    private static SchedulingGenerationOptions OptionsFor(ObjectiveMode mode) => new()
    {
        SolverMode = SchedulingSolverMode.Csp,
        Csp = new CspOptions
        {
            Mode = mode,
            WeightedMaxNodes = mode == ObjectiveMode.WeightedUtility ? 50_000 : null,
            TimeLimitMilliseconds = 15_000
        }
    };

    private static async Task<Execution> ExecuteOnceAsync(
        GoldenScenario scenario,
        GenerationInput input,
        SchedulingGenerationOptions options,
        WeightedRequestInputs? weighted,
        CancellationToken cancellationToken)
    {
        OptimizationResult? optimization = null;
        var service = new ItineraryGenerationService(
            new PrecomputedRouteDurationProvider(scenario.Matrix),
            options)
        {
            OnOptimizationCompleted = value => optimization = value
        };
        var stopwatch = Stopwatch.StartNew();
        var result = await service.GenerateAsync(input, weighted, cancellationToken);
        stopwatch.Stop();

        if (!result.IsSuccess || optimization is null)
        {
            return new Execution(false, null, stopwatch.Elapsed.TotalMilliseconds);
        }

        var schedule = optimization.Schedule with { Plan = result.Value };
        return new Execution(true, schedule, stopwatch.Elapsed.TotalMilliseconds);
    }

    private sealed record Execution(bool Feasible, EvaluatedItinerarySchedule? Schedule, double ElapsedMilliseconds);

    private sealed class PrecomputedRouteDurationProvider(RouteDurationMatrix matrix) : IRouteDurationProvider
    {
        public Task<RouteDurationMatrix> GetMatrixAsync(
            IReadOnlyList<RoutePoint> points,
            TransportMode transportMode,
            CancellationToken cancellationToken) =>
            Task.FromResult(matrix);
    }
}