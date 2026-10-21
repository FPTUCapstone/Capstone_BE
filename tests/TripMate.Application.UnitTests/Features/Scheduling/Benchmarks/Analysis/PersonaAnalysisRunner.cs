using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Common.Models;
using TripMate.Application.Features.Scheduling.Common;
using TripMate.Application.Features.Scheduling.Csp;
using TripMate.Application.Features.Scheduling.Csp.Objectives;
using TripMate.Application.Features.Scheduling.Personalization;
using TripMate.Application.Features.Scheduling.TimeOfDay;
using TripMate.Application.UnitTests.Features.Scheduling.Golden;
using TripMate.Application.UnitTests.Features.Scheduling.Benchmarks.Shadow;
using TripMate.Domain.Constants;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;
using TripMate.Infrastructure.Persistence;

namespace TripMate.Application.UnitTests.Features.Scheduling.Benchmarks.Analysis;

public sealed record PersonaPersonalizationResult(
    GenerationCandidate[] Candidates,
    PoiRankingOutcomeCategory OutcomeCategory,
    bool FallbackUsed,
    string ProviderMode);

public static class PersonaAnalysisRunner
{
    private static ApplicationDbContext CreateBenchmarkDbContext()
    {
        var dbOptions = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new ApplicationDbContext(dbOptions);
    }

    public static async Task<PersonaPersonalizationResult> PersonalizeCandidatesAsync(
        IReadOnlyList<GenerationCandidate> candidates,
        BenchmarkPersonaData persona,
        IPoiRankingProvider provider,
        PersonalizationRankingOptions rankingOptions,
        bool providerEnabled,
        string providerMode,
        CancellationToken cancellationToken)
    {
        using var dbContext = CreateBenchmarkDbContext();
        var orchestrator = new PoiRankingOrchestrator(
            new PersonalBehaviorFeatureAggregator(dbContext),
            provider,
            rankingOptions,
            providerEnabled,
            NullLogger<PoiRankingOrchestrator>.Instance);

        var inputCandidates = candidates.Select(c => new PoiRankingInputCandidate(
            c.Id,
            (int)(c.Id % 100),
            c.Name,
            c.CategoryName ?? PoiCategoryNames.General,
            c.Tags ?? [],
            c.ScenicScore,
            c.PhotoRating,
            0m,
            c.EstimatedVisitCost)).ToArray();

        var input = new PoiRankingInput(1001L, persona.Tags, inputCandidates);
        var ranking = await orchestrator.BuildRankingAsync(input, cancellationToken);

        var personalizedCandidates = candidates.Select(c =>
        {
            var entry = ranking.Entries[c.Id];
            return c with
            {
                TripMateBaseScore = entry.TripMateBaseScore,
                EffectiveDesirabilityScore = entry.EffectiveDesirabilityScore
            };
        }).ToArray();

        var fallbackUsed = ranking.OutcomeCategory != PoiRankingOutcomeCategory.Success;

        return new PersonaPersonalizationResult(
            personalizedCandidates,
            ranking.OutcomeCategory,
            fallbackUsed,
            providerMode);
    }

    public static async Task<PersonaAnalysisRunResult> RunAsync(
        CityBenchmarkScenarioData? cityData = null,
        IPoiRankingProvider? provider = null,
        PersonalizationRankingOptions? rankingOptions = null,
        string evaluationMode = "recorded",
        double jaccardThreshold = 0.40,
        CancellationToken cancellationToken = default)
    {
        cityData ??= SchedulingBenchmarkScenarioFactory.CreateDaNangV3bData();
        provider ??= new RecordedPoiRankingProvider();
        rankingOptions ??= new PersonalizationRankingOptions();

        var scenario = SchedulingBenchmarkScenarioFactory.CreateScenario(cityData);
        var options = ShadowBenchmarkExecutor.OptionsFor(ObjectiveMode.WeightedUtility);

        // One authoritative personalization snapshot per persona is reused by every evaluation.
        var personalizationByPersona = new Dictionary<string, PersonaPersonalizationResult>(cityData.Personas.Count);
        var successCount = 0;
        var skippedCount = 0;
        var fallbackCount = 0;
        foreach (var persona in cityData.Personas)
        {
            var personalization = await PersonalizeCandidatesAsync(
                scenario.Candidates,
                persona,
                provider,
                rankingOptions,
                providerEnabled: true,
                providerMode: evaluationMode,
                cancellationToken);

            if (personalization.OutcomeCategory == PoiRankingOutcomeCategory.Success)
            {
                successCount++;
            }
            else if (personalization.OutcomeCategory == PoiRankingOutcomeCategory.ProviderSkipped)
            {
                skippedCount++;
            }
            else
            {
                fallbackCount++;
            }

            if (evaluationMode.Equals("live", StringComparison.OrdinalIgnoreCase))
            {
                var requiredOutcome = persona.Tags.Count > 0
                    ? PoiRankingOutcomeCategory.Success
                    : PoiRankingOutcomeCategory.ProviderSkipped;
                if (personalization.OutcomeCategory != requiredOutcome)
                {
                    throw new InvalidOperationException(
                        $"BLOCKED: live BGE-M3 ranking did not complete successfully for persona '{persona.Id}' (Outcome: {personalization.OutcomeCategory}).");
                }
            }

            personalizationByPersona.Add(persona.Id, personalization);
        }

        // 1. Run baselines for all 5 personas using the authoritative personalization snapshots.
        var baselineMap = new Dictionary<string, (PersonaBaselineResult Result, IReadOnlyList<long> VisitedPoiIds)>();
        var baselineList = new List<PersonaBaselineResult>(cityData.Personas.Count);
        foreach (var persona in cityData.Personas)
        {
            var personalization = personalizationByPersona[persona.Id];

            var candidates = personalization.Candidates;
            var input = scenario.Input with { Candidates = candidates };
            var profile = SchedulingBenchmarkScenarioFactory.CreateProfile(persona, scenario.Input.StartAtUtc);
            var weightedFactory = new WeightedRequestInputsFactory(new PersonalParamsFactory(
                Options.Create(SchedulingBenchmarkScenarioFactory.CreatePersonalizationOptions())));
            var weighted = weightedFactory.Create(candidates, profile, input, personalization.OutcomeCategory);

            var execution = await ShadowBenchmarkExecutor.ExecuteOnceAsync(scenario, input, options, weighted, cancellationToken);
            if (!execution.Feasible || execution.Schedule is null)
            {
                var failed = new PersonaBaselineResult(persona.Id, persona.DisplayName, false, [], 0, 0, 0, 0, null);
                baselineList.Add(failed);
                baselineMap[persona.Id] = (failed, []);
                continue;
            }

            var candidateMap = candidates.ToDictionary(c => c.Id);
            var metrics = ShadowBenchmarkMetrics.Create(
                scenario.Id, persona.Id, ObjectiveMode.WeightedUtility, execution.Schedule, candidateMap, input.TimeZone, [execution.ElapsedMilliseconds]);

            var baseline = new PersonaBaselineResult(
                persona.Id,
                persona.DisplayName,
                true,
                metrics.VisitedPoiIds,
                metrics.StopCount,
                metrics.TotalMatrixTravelMinutes,
                metrics.DiversityGroupCount,
                metrics.MiddayOutdoorVisitCount,
                metrics.ModelObjective);

            baselineList.Add(baseline);
            baselineMap[persona.Id] = (baseline, metrics.VisitedPoiIds);
        }

        // 2. Build 5x5 Jaccard matrix and pairs
        var personaIds = cityData.Personas.Select(p => p.Id).ToArray();
        var n = personaIds.Length;
        var matrix = new double[n, n];
        var pairs = new List<PersonaJaccardPair>((n * (n - 1)) / 2);
        var belowThreshold = new List<PersonaJaccardPair>();
        var aboveOrEqualThreshold = new List<PersonaJaccardPair>();

        for (var i = 0; i < n; i++)
        {
            matrix[i, i] = 1.00;
            var idA = personaIds[i];
            var visitsA = baselineMap[idA].VisitedPoiIds;

            for (var j = i + 1; j < n; j++)
            {
                var idB = personaIds[j];
                var visitsB = baselineMap[idB].VisitedPoiIds;

                var jaccard = PersonaJaccard.Calculate(visitsA, visitsB);
                var (intersection, union) = PersonaJaccard.Counts(visitsA, visitsB);

                matrix[i, j] = jaccard;
                matrix[j, i] = jaccard;

                var pair = new PersonaJaccardPair(
                    idA,
                    idB,
                    visitsA,
                    visitsB,
                    visitsA.Count,
                    visitsB.Count,
                    intersection,
                    union,
                    jaccard);

                pairs.Add(pair);
                if (jaccard < jaccardThreshold)
                {
                    belowThreshold.Add(pair);
                }
                else
                {
                    aboveOrEqualThreshold.Add(pair);
                }
            }
        }

        var jaccardMatrix = new PersonaJaccardMatrix(personaIds, matrix, pairs);

        // 3. Run Ablation study (6 variants across 5 personas = 30 evaluations)
        var ablations = new List<AblationVariantResult>();
        foreach (var persona in cityData.Personas)
        {
            var fullVisited = baselineMap[persona.Id].VisitedPoiIds;
            var personalization = personalizationByPersona[persona.Id];
            var candidates = personalization.Candidates;
            var candidateMap = candidates.ToDictionary(c => c.Id);
            var input = scenario.Input with { Candidates = candidates };
            var profile = SchedulingBenchmarkScenarioFactory.CreateProfile(persona, scenario.Input.StartAtUtc);
            var defaultParams = new PersonalParamsFactory(
                Options.Create(SchedulingBenchmarkScenarioFactory.CreatePersonalizationOptions())).Create(profile);

            // Variant 1: Base-only ablation (raw TripMateBaseScore without AI blend)
            {
                var baseOnlyCandidates = candidates.Select(c => c with
                {
                    EffectiveDesirabilityScore = c.TripMateBaseScore
                }).ToArray();
                var baseOnlyInput = scenario.Input with { Candidates = baseOnlyCandidates };
                var baseOnlyCandidateMap = baseOnlyCandidates.ToDictionary(c => c.Id);
                var weighted = new WeightedRequestInputs(
                    baseOnlyCandidates.ToDictionary(c => c.Id, c => (double)c.EffectiveDesirabilityScore!.Value),
                    defaultParams,
                    new RealTimeFitProviderFactory(defaultParams),
                    "base-only-ablation");

                ablations.Add(await ExecuteAndEvaluateAsync(scenario, baseOnlyInput, options, weighted, exec => EvaluateAblation(
                    "Base-only ablation",
                    "Raw TripMateBaseScore without AI score blend (BaseWeight 1.0, AiWeight 0.0)",
                    persona.Id,
                    fullVisited,
                    exec,
                    scenario,
                    baseOnlyCandidateMap,
                    baseOnlyInput.TimeZone,
                    "Evaluates itinerary selection driven purely by deterministic heuristic base score"), cancellationToken));
            }

            // Variant 2: Production fallback with provider disabled
            {
                var fallbackResult = await PersonalizeCandidatesAsync(
                    scenario.Candidates,
                    persona,
                    provider,
                    rankingOptions,
                    providerEnabled: false,
                    providerMode: evaluationMode,
                    cancellationToken);
                var fallbackCandidates = fallbackResult.Candidates;
                var fallbackInput = scenario.Input with { Candidates = fallbackCandidates };
                var fallbackCandidateMap = fallbackCandidates.ToDictionary(c => c.Id);
                var weighted = new WeightedRequestInputs(
                    fallbackCandidates.ToDictionary(c => c.Id, c => (double)c.EffectiveDesirabilityScore!.Value),
                    defaultParams,
                    new RealTimeFitProviderFactory(defaultParams),
                    "production-fallback");

                ablations.Add(await ExecuteAndEvaluateAsync(scenario, fallbackInput, options, weighted, exec => EvaluateAblation(
                    "Production fallback",
                    FormattableString.Invariant($"Production fallback AI score ({rankingOptions.PersonalizedAiFallbackScore:0.00} for personalized, {rankingOptions.DefaultAiFallbackScore:0.00} for default) with provider disabled"),
                    persona.Id,
                    fullVisited,
                    exec,
                    scenario,
                    fallbackCandidateMap,
                    fallbackInput.TimeZone,
                    "Evaluates itinerary selection when AI provider is offline and deterministic fallback score is applied"), cancellationToken));
            }

            // Variant 3: No category decay (GammaLoved = 1.0, GammaDefault = 1.0)
            {
                var noDecayParams = defaultParams with { GammaLoved = 1.0, GammaDefault = 1.0 };
                var weighted = new WeightedRequestInputs(
                    candidates.ToDictionary(c => c.Id, c => (double)c.EffectiveDesirabilityScore!.Value),
                    noDecayParams,
                    new RealTimeFitProviderFactory(noDecayParams),
                    "no-category-decay");

                ablations.Add(await ExecuteAndEvaluateAsync(scenario, input, options, weighted, exec => EvaluateAblation(
                    "No category decay",
                    "GammaLoved = 1.0, GammaDefault = 1.0 (no diminishing utility for same-group POIs)",
                    persona.Id,
                    fullVisited,
                    exec,
                    scenario,
                    candidateMap,
                    input.TimeZone,
                    "Diminishing returns removed; repeats within same diversity group face no discount"), cancellationToken));
            }

            // Variant 4: No soft rho penalties (RhoStops = 0, RhoOutdoor = 0, RhoRest = 0, RhoEffortPerHour = 0)
            {
                var noRhoParams = defaultParams with
                {
                    RhoStops = 0.0,
                    RhoOutdoor = 0.0,
                    RhoRest = 0.0,
                    RhoEffortPerHour = 0.0
                };
                var weighted = new WeightedRequestInputs(
                    candidates.ToDictionary(c => c.Id, c => (double)c.EffectiveDesirabilityScore!.Value),
                    noRhoParams,
                    new RealTimeFitProviderFactory(noRhoParams),
                    "no-rho-penalties");

                ablations.Add(await ExecuteAndEvaluateAsync(scenario, input, options, weighted, exec => EvaluateAblation(
                    "No soft rho penalties",
                    "RhoStops = 0, RhoOutdoor = 0, RhoRest = 0, RhoEffortPerHour = 0",
                    persona.Id,
                    fullVisited,
                    exec,
                    scenario,
                    candidateMap,
                    input.TimeZone,
                    "Soft pacing/effort/outdoor penalties neutralized; hard limits and travel cost remain active"), cancellationToken));
            }

            // Variant 5: No travel penalty (LambdaPerMin = 0)
            {
                var noTravelParams = defaultParams with { LambdaPerMin = 0.0 };
                var weighted = new WeightedRequestInputs(
                    candidates.ToDictionary(c => c.Id, c => (double)c.EffectiveDesirabilityScore!.Value),
                    noTravelParams,
                    new RealTimeFitProviderFactory(noTravelParams),
                    "no-travel-penalty");

                ablations.Add(await ExecuteAndEvaluateAsync(scenario, input, options, weighted, exec => EvaluateAblation(
                    "No travel penalty",
                    "LambdaPerMin = 0 (travel duration cost zeroed in objective)",
                    persona.Id,
                    fullVisited,
                    exec,
                    scenario,
                    candidateMap,
                    input.TimeZone,
                    "Travel minutes ignored by objective function; solver only constrained by available day minutes"), cancellationToken));
            }

            // Variant 6: No TimeFit (ConstantTimeFitFactory returning 1.0)
            {
                var weighted = new WeightedRequestInputs(
                    candidates.ToDictionary(c => c.Id, c => (double)c.EffectiveDesirabilityScore!.Value),
                    defaultParams,
                    new ConstantTimeFitFactory(),
                    "no-timefit");

                ablations.Add(await ExecuteAndEvaluateAsync(scenario, input, options, weighted, exec => EvaluateAblation(
                    "No TimeFit",
                    "ConstantTimeFit = 1.0 for all nodes and intervals",
                    persona.Id,
                    fullVisited,
                    exec,
                    scenario,
                    candidateMap,
                    input.TimeZone,
                    "Diurnal preference curve removed; arrival time does not scale POI desirability"), cancellationToken));
            }
        }

        // 4. Run Sensitivity Analysis (one-at-a-time across personas = 105 probe points)
        var sensitivities = new List<SensitivityPointResult>();
        foreach (var persona in cityData.Personas)
        {
            var baseVisited = baselineMap[persona.Id].VisitedPoiIds;
            var personalization = personalizationByPersona[persona.Id];
            var candidates = personalization.Candidates;
            var candidateMap = candidates.ToDictionary(c => c.Id);
            var input = scenario.Input with { Candidates = candidates };
            var profile = SchedulingBenchmarkScenarioFactory.CreateProfile(persona, scenario.Input.StartAtUtc);
            var baseParams = new PersonalParamsFactory(
                Options.Create(SchedulingBenchmarkScenarioFactory.CreatePersonalizationOptions())).Create(profile);
            var baseScores = candidates.ToDictionary(c => c.Id, c => (double)c.EffectiveDesirabilityScore!.Value);

            // A. Lambda sensitivity (0.5x, 1.0x, 1.5x)
            var lambdaBase = baseParams.LambdaPerMin;
            var lambdaPoints = new (string Level, double Multiplier)[]
            {
                ("Low", 0.5),
                ("Base", 1.0),
                ("High", 1.5)
            };
            foreach (var (level, mult) in lambdaPoints)
            {
                var val = lambdaBase * mult;
                var p = baseParams with { LambdaPerMin = val };
                var w = new WeightedRequestInputs(baseScores, p, new RealTimeFitProviderFactory(p), $"lambda-{level}");
                sensitivities.Add(await ExecuteAndEvaluateAsync(scenario, input, options, w, exec => EvaluateSensitivity("LambdaPerMin", level, val, persona.Id, baseVisited, exec, scenario, candidateMap, input.TimeZone, FormattableString.Invariant($"Travel cost sensitivity probe ({mult:0.0}x base)")), cancellationToken));
            }

            // B. GammaDefault sensitivity (Low: 0.50, Base: 0.70, High: 0.90)
            var gammaDefaultPoints = new (string Level, double Value)[]
            {
                ("Low", 0.50),
                ("Base", 0.70),
                ("High", 0.90)
            };
            foreach (var (level, val) in gammaDefaultPoints)
            {
                var p = baseParams with { GammaDefault = val };
                var w = new WeightedRequestInputs(baseScores, p, new RealTimeFitProviderFactory(p), $"gamma-default-{level}");
                sensitivities.Add(await ExecuteAndEvaluateAsync(scenario, input, options, w, exec => EvaluateSensitivity("GammaDefault", level, val, persona.Id, baseVisited, exec, scenario, candidateMap, input.TimeZone, "Diminishing marginal utility discount for non-loved categories"), cancellationToken));
            }

            // C. GammaLoved sensitivity (Low: 0.75, Base: 0.90, High: 1.00)
            var gammaLovedPoints = new (string Level, double Value)[]
            {
                ("Low", 0.75),
                ("Base", 0.90),
                ("High", 1.00)
            };
            foreach (var (level, val) in gammaLovedPoints)
            {
                var p = baseParams with { GammaLoved = val };
                var w = new WeightedRequestInputs(baseScores, p, new RealTimeFitProviderFactory(p), $"gamma-loved-{level}");
                sensitivities.Add(await ExecuteAndEvaluateAsync(scenario, input, options, w, exec => EvaluateSensitivity("GammaLoved", level, val, persona.Id, baseVisited, exec, scenario, candidateMap, input.TimeZone, "Diminishing marginal utility discount for loved categories"), cancellationToken));
            }

            // D. RhoStops sensitivity (Low: 0.15, Base: 0.30, High: 0.45)
            var rhoStopsPoints = new (string Level, double Multiplier)[]
            {
                ("Low", 0.5),
                ("Base", 1.0),
                ("High", 1.5)
            };
            foreach (var (level, mult) in rhoStopsPoints)
            {
                var val = baseParams.RhoStops * mult;
                var p = baseParams with { RhoStops = val };
                var w = new WeightedRequestInputs(baseScores, p, new RealTimeFitProviderFactory(p), $"rho-stops-{level}");
                sensitivities.Add(await ExecuteAndEvaluateAsync(scenario, input, options, w, exec => EvaluateSensitivity("RhoStops", level, val, persona.Id, baseVisited, exec, scenario, candidateMap, input.TimeZone, FormattableString.Invariant($"Soft penalty per stop exceeding pace max ({mult:0.0}x base)")), cancellationToken));
            }

            // E. RhoOutdoor sensitivity (Low: 0.125, Base: 0.25, High: 0.375)
            var rhoOutdoorPoints = new (string Level, double Multiplier)[]
            {
                ("Low", 0.5),
                ("Base", 1.0),
                ("High", 1.5)
            };
            foreach (var (level, mult) in rhoOutdoorPoints)
            {
                var val = baseParams.RhoOutdoor * mult;
                var p = baseParams with { RhoOutdoor = val };
                var w = new WeightedRequestInputs(baseScores, p, new RealTimeFitProviderFactory(p), $"rho-outdoor-{level}");
                sensitivities.Add(await ExecuteAndEvaluateAsync(scenario, input, options, w, exec => EvaluateSensitivity("RhoOutdoor", level, val, persona.Id, baseVisited, exec, scenario, candidateMap, input.TimeZone, FormattableString.Invariant($"Soft penalty for consecutive unsheltered outdoor visits ({mult:0.0}x base)")), cancellationToken));
            }

            // F. RhoRest sensitivity (Baseline = 0.0 -> Inactive at baseline)
            var rhoRestPoints = new (string Level, double Value, string Notes)[]
            {
                ("Low", 0.0, "Inactive at baseline (baseline = 0.0)"),
                ("Base", 0.0, "Inactive at baseline (baseline = 0.0)"),
                ("High", 0.10, "Exploratory probe for non-zero rest penalty")
            };
            foreach (var (level, val, note) in rhoRestPoints)
            {
                var p = baseParams with { RhoRest = val };
                var w = new WeightedRequestInputs(baseScores, p, new RealTimeFitProviderFactory(p), $"rho-rest-{level}");
                sensitivities.Add(await ExecuteAndEvaluateAsync(scenario, input, options, w, exec => EvaluateSensitivity("RhoRest", level, val, persona.Id, baseVisited, exec, scenario, candidateMap, input.TimeZone, note), cancellationToken));
            }

            // G. RhoEffortPerHour sensitivity (Low: 0.025, Base: 0.05, High: 0.075)
            var rhoEffortPoints = new (string Level, double Multiplier)[]
            {
                ("Low", 0.5),
                ("Base", 1.0),
                ("High", 1.5)
            };
            foreach (var (level, mult) in rhoEffortPoints)
            {
                var val = baseParams.RhoEffortPerHour * mult;
                var p = baseParams with { RhoEffortPerHour = val };
                var w = new WeightedRequestInputs(baseScores, p, new RealTimeFitProviderFactory(p), $"rho-effort-{level}");
                sensitivities.Add(await ExecuteAndEvaluateAsync(scenario, input, options, w, exec => EvaluateSensitivity("RhoEffortPerHour", level, val, persona.Id, baseVisited, exec, scenario, candidateMap, input.TimeZone, FormattableString.Invariant($"Soft penalty per hour of effort exceeding daily budget ({mult:0.0}x base)")), cancellationToken));
            }
        }

        return new PersonaAnalysisRunResult(
            cityData,
            baselineList,
            jaccardMatrix,
            ablations,
            sensitivities,
            belowThreshold,
            aboveOrEqualThreshold,
            successCount,
            skippedCount,
            fallbackCount);
    }

    private static async Task<T> ExecuteAndEvaluateAsync<T>(
        GoldenScenario scenario,
        GenerationInput input,
        SchedulingGenerationOptions options,
        WeightedRequestInputs weighted,
        Func<ShadowBenchmarkExecutor.Execution, T> evaluate,
        CancellationToken cancellationToken)
    {
        var execution = await ShadowBenchmarkExecutor.ExecuteOnceAsync(
            scenario,
            input,
            options,
            weighted,
            cancellationToken);
        return evaluate(execution);
    }

    private static AblationVariantResult EvaluateAblation(
        string variantName,
        string description,
        string personaId,
        IReadOnlyList<long> fullVisited,
        ShadowBenchmarkExecutor.Execution exec,
        Golden.GoldenScenario scenario,
        IReadOnlyDictionary<long, GenerationCandidate> candidateMap,
        TimeZoneInfo timeZone,
        string notes)
    {
        if (!exec.Feasible || exec.Schedule is null)
        {
            return new AblationVariantResult(
                variantName, description, personaId, false, [], 0.0, [], fullVisited, 0, 0, 0, 0, null, notes);
        }

        var metrics = ShadowBenchmarkMetrics.Create(
            scenario.Id, personaId, ObjectiveMode.WeightedUtility, exec.Schedule, candidateMap, timeZone, [exec.ElapsedMilliseconds]);

        var jaccard = PersonaJaccard.Calculate(fullVisited, metrics.VisitedPoiIds);
        var added = metrics.VisitedPoiIds.Except(fullVisited).ToArray();
        var removed = fullVisited.Except(metrics.VisitedPoiIds).ToArray();

        return new AblationVariantResult(
            variantName,
            description,
            personaId,
            true,
            metrics.VisitedPoiIds,
            jaccard,
            added,
            removed,
            metrics.StopCount,
            metrics.TotalMatrixTravelMinutes,
            metrics.DiversityGroupCount,
            metrics.MiddayOutdoorVisitCount,
            metrics.ModelObjective,
            notes);
    }

    private static SensitivityPointResult EvaluateSensitivity(
        string parameterName,
        string level,
        double value,
        string personaId,
        IReadOnlyList<long> baseVisited,
        ShadowBenchmarkExecutor.Execution exec,
        Golden.GoldenScenario scenario,
        IReadOnlyDictionary<long, GenerationCandidate> candidateMap,
        TimeZoneInfo timeZone,
        string notes)
    {
        if (!exec.Feasible || exec.Schedule is null)
        {
            return new SensitivityPointResult(
                parameterName, level, value, personaId, false, [], 0.0, 0, 0, 0, 0, null, notes);
        }

        var metrics = ShadowBenchmarkMetrics.Create(
            scenario.Id, personaId, ObjectiveMode.WeightedUtility, exec.Schedule, candidateMap, timeZone, [exec.ElapsedMilliseconds]);

        var jaccard = PersonaJaccard.Calculate(baseVisited, metrics.VisitedPoiIds);

        return new SensitivityPointResult(
            parameterName,
            level,
            value,
            personaId,
            true,
            metrics.VisitedPoiIds,
            jaccard,
            metrics.StopCount,
            metrics.TotalMatrixTravelMinutes,
            metrics.DiversityGroupCount,
            metrics.MiddayOutdoorVisitCount,
            metrics.ModelObjective,
            notes);
    }
}