using System.Globalization;
using System.Text;

using TripMate.Domain.Enums;

namespace TripMate.Application.UnitTests.Features.Scheduling.Benchmarks.Analysis;

public sealed record PersonaAnalysisReportMetadata(
    string RunDate,
    string BuildConfiguration,
    string OperatingSystem,
    string Runtime,
    string Machine,
    int LogicalProcessorCount,
    decimal BaseWeight,
    decimal AiWeight,
    decimal PersonalizedAiFallbackScore,
    decimal DefaultAiFallbackScore,
    string EvaluationMode = "recorded",
    string ModelName = "bge-m3:latest",
    string FixtureSha256 = "N/A (Not provided)",
    string Endpoint = "Recorded fixture (local deterministic)",
    string GitCommitHash = "b7eba74ddc8afe3521d860c62eb83976426718d3",
    string GitWorkingTreeState = "clean",
    int SuccessCount = 4,
    int ProviderSkippedCount = 1,
    int FallbackCount = 0,
    double JaccardThreshold = 0.40,
    decimal CategoryAffinityWeight = 0.450m,
    decimal TagAffinityWeight = 0.350m,
    decimal BehaviorAffinityWeight = 0.100m,
    decimal ScenicQualityWeight = 0.050m,
    decimal PhotoQualityWeight = 0.050m);

public static class PersonaAnalysisMarkdownRenderer
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public static string Render(PersonaAnalysisReportMetadata metadata, PersonaAnalysisRunResult result)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        ArgumentNullException.ThrowIfNull(result);

        var sb = new StringBuilder();
        var threshold = metadata.JaccardThreshold;
        var thresholdText = threshold.ToString("0.00", Inv);
        var baseWeight = metadata.BaseWeight.ToString("0.00", Inv);
        var aiWeight = metadata.AiWeight.ToString("0.00", Inv);
        var personalizedFallback = metadata.PersonalizedAiFallbackScore.ToString("0.00", Inv);
        var defaultFallback = metadata.DefaultAiFallbackScore.ToString("0.00", Inv);
        var pairGate = result.ClassifyPairs(threshold);
        var personalizedPairsBelowThreshold = pairGate.PersonalizedPairsBelowThreshold;
        var personalizedPairsAboveOrEqualThreshold = pairGate.PersonalizedPairsAboveOrEqualThreshold;
        var coldStartPairs = pairGate.ColdStartPairs;
        var personalizedPairCount = personalizedPairsBelowThreshold.Count
            + personalizedPairsAboveOrEqualThreshold.Count;

        // Title and Status
        sb.Append("# UC-10 Personalization Verification: Persona Jaccard, Ablation Study & Sensitivity Analysis\n\n");
        sb.Append("> **Status**: ");
        if (personalizedPairsAboveOrEqualThreshold.Count == 0
            && personalizedPairCount > 0
            && result.ColdStartGatePassed)
        {
            sb.Append($"**PASS — PERSONALIZED PAIRS MEET JACCARD GATE (< {thresholdText}) AND COLD-START QUALITY GATE**\n\n");
        }
        else
        {
            sb.Append("**MEASUREMENT COMPLETE — PERSONALIZATION GATE NOT MET**\n\n");
        }

        // Executive Summary
        sb.Append("## 1. Executive Summary & Metadata\n\n");
        sb.Append($"- **Run Date**: {metadata.RunDate}\n");
        sb.Append($"- **Configuration**: {metadata.BuildConfiguration}\n");
        sb.Append($"- **OS / Runtime**: {metadata.OperatingSystem} / {metadata.Runtime}\n");
        sb.Append($"- **Machine**: {metadata.Machine} ({metadata.LogicalProcessorCount} logical cores)\n");
        sb.Append($"- **Git Commit**: `{metadata.GitCommitHash}` ({metadata.GitWorkingTreeState})\n");
        sb.Append($"- **Evaluation Mode**: {(metadata.EvaluationMode.Equals("live", StringComparison.OrdinalIgnoreCase) ? "Live BGE-M3 Execution" : "Recorded BGE-M3 Replay (deterministic)")}\n");
        sb.Append($"- **Configured Embedding Model**: `{metadata.ModelName}`\n");
        sb.Append("- **Runtime Model Identity**: not exposed by provider contract\n");
        if (!metadata.EvaluationMode.Equals("live", StringComparison.OrdinalIgnoreCase))
        {
            sb.Append($"- **Fixture Provenance SHA-256**: `{metadata.FixtureSha256}`\n");
        }
        sb.Append($"- **Service Endpoint**: `{metadata.Endpoint}`\n");
        sb.Append($"- **Target Scenario**: `{result.CityData.Id}` ({result.CityData.Pois.Count} POIs)\n");
        sb.Append($"- **Objective Mode**: `WeightedUtility` (Csp)\n");
        sb.Append($"- **Personalization Weights**: BaseWeight = {baseWeight}, AiWeight = {aiWeight}\n");
        sb.Append($"- **Personalization Gate Threshold**: Jaccard < {metadata.JaccardThreshold.ToString("0.00", Inv)}\n");
        sb.Append($"- **Baseline Provider Outcomes**: {metadata.SuccessCount} Success, {metadata.ProviderSkippedCount} ProviderSkipped, {metadata.FallbackCount} Fallback\n");
        sb.Append($"- **Personas Evaluated**: {result.Baselines.Count}\n");
        sb.Append($"- **Total Persona Pairs**: {result.JaccardMatrix.Pairs.Count}\n");
        sb.Append($"- **Personalized Pairs Meeting Gate (< {thresholdText})**: {personalizedPairsBelowThreshold.Count} / {personalizedPairCount}\n");
        sb.Append($"- **Personalized Pairs Exceeding Gate (>= {thresholdText})**: {personalizedPairsAboveOrEqualThreshold.Count} / {personalizedPairCount}\n");
        sb.Append($"- **Cold-Start Comparison Pairs (observational)**: {coldStartPairs.Count}\n");
        sb.Append($"- **Cold-Start Quality Gate**: {(result.ColdStartGatePassed ? "PASS" : "FAIL")} (feasible, non-empty, at least 2 diversity groups)\n");
        sb.Append($"- **Ablation Variants**: {result.Ablations.Select(a => a.VariantName).Distinct().Count()} variants across {result.Baselines.Count} personas ({result.Ablations.Count} runs)\n");
        sb.Append($"- **Sensitivity Probes**: {result.Sensitivities.Select(s => s.ParameterName).Distinct().Count()} parameters ({result.Sensitivities.Count} runs)\n\n");

        // Section 2: Data-Flow Audit
        sb.Append("## 2. End-to-End Data-Flow Audit (Like/Dislike/Skip & Personalization)\n\n");
        sb.Append("### 2.1 Production Scheduling Pipeline\n\n");
        sb.Append("The end-to-end data flow for personalization in production was verified from source code:\n\n");
        sb.Append("```text\n");
        sb.Append("1. User Interactions (Like, Dislike, Skip)\n");
        sb.Append("   -> RecommendationBehaviorEvents table (PostgreSQL/EF Core)\n");
        sb.Append('\n');
        sb.Append("2. CreateSchedulingRequestCommandHandler\n");
        sb.Append("   -> PoiRankingOrchestrator.BuildRankingAsync(PoiRankingInput)\n");
        sb.Append('\n');
        sb.Append("3. PersonalBehaviorFeatureAggregator.AggregateAsync\n");
        sb.Append("   -> Grouped counts by POI ID and Category ID\n");
        sb.Append('\n');
        sb.Append("4. PersonalBehaviorAffinityScorer.Score(poiId, categoryId, aggregation)\n");
        sb.Append("   -> Computes behaviorAffinity in [0, 1] (PriorWeight=2.0, SkipWeight=0.5, Neutral=0.5)\n");
        sb.Append('\n');
        sb.Append("5. PersonalizationBaseScorer.Score(features, behaviorAffinity)\n");
        sb.Append($"   -> BaseScore = {metadata.CategoryAffinityWeight.ToString("0.000", Inv)}*CatAffinity + {metadata.TagAffinityWeight.ToString("0.000", Inv)}*TagAffinity + {metadata.BehaviorAffinityWeight.ToString("0.000", Inv)}*BehaviorAffinity + {metadata.ScenicQualityWeight.ToString("0.000", Inv)}*Scenic + {metadata.PhotoQualityWeight.ToString("0.000", Inv)}*Photo\n");
        sb.Append('\n');
        sb.Append("6. AI Blending / Fallback\n");
        sb.Append($"   -> EffectiveDesirabilityScore = {baseWeight} * BaseScore + {aiWeight} * AiScore\n");
        sb.Append($"      (When AI disabled/skipped/timed out: fallbackAiScore = {personalizedFallback} for personalized, {defaultFallback} for default)\n");
        sb.Append('\n');
        sb.Append("7. WeightedRequestInputsFactory.Create\n");
        sb.Append("   -> Extracts EffectiveDesirabilityScore as BaseByPoiId\n");
        sb.Append("   -> PersonalParamsFactory creates lambda, gamma, rho from TravelerProfile\n");
        sb.Append("   -> RealTimeFitProviderFactory builds diurnal time-of-day suitability curves\n");
        sb.Append('\n');
        sb.Append("8. CspItinerarySolver (WeightedUtility Objective)\n");
        sb.Append("   -> Objective = sum(Base * TimeFit * gamma^k) - lambda * TravelMinutes - soft rho penalties\n");
        sb.Append("```\n\n");

        sb.Append("### 2.2 Reachability in Benchmark Environment\n\n");
        sb.Append("- **Production Status**: **Fully wired**. The code path from database events through affinity scoring to CSP objective inputs is completely implemented in production.\n");
        sb.Append("- **Benchmark Status**: **Neutral baseline**. The shadow benchmark corpus operates purely in memory without an active database or historical event seeds. Consequently, `aggregation` has 0 events and `PersonalBehaviorAffinityScorer` returns `NeutralAffinity = 0.5m`. There is no fake data injected.\n\n");

        // Section 3: Evaluated Personas
        sb.Append("## 3. Personas Evaluated\n\n");
        sb.Append("| Persona ID | Display Name | Pacing | Risk | Preference Tags | Categories | Description |\n");
        sb.Append("|---|---|---|---|---|---|---|\n");
        foreach (var p in result.CityData.Personas)
        {
            var tagsStr = p.Tags.Count > 0 ? string.Join(", ", p.Tags) : "(none / cold start)";
            var catsStr = p.LovedCategories.Count > 0 ? string.Join(", ", p.LovedCategories) : "(none)";
            sb.Append($"| `{p.Id}` | {p.DisplayName} | {p.Pace} | {p.Risk} | {tagsStr} | {catsStr} | {p.Description} |\n");
        }
        sb.Append("\n");

        // Section 4: Persona Baseline Itineraries
        sb.Append("## 4. Persona Baseline Itineraries (Csp, WeightedUtility Mode)\n\n");
        sb.Append("| Persona | Feasible | Stops | Travel (min) | Diversity Groups | Midday Outdoor | Model Objective | Visited POI IDs |\n");
        sb.Append("|---|---|---:|---:|---:|---:|---:|---|\n");
        foreach (var b in result.Baselines)
        {
            var visitedStr = b.VisitedPoiIds.Count > 0 ? string.Join(",", b.VisitedPoiIds) : "None";
            sb.Append($"| `{b.PersonaId}` | {(b.Feasible ? "Feasible" : "Infeasible")} | {b.StopCount} | {b.TotalMatrixTravelMinutes} | {b.DiversityGroupCount} | {b.MiddayOutdoorVisitCount} | {b.ModelObjective?.ToString(Inv) ?? "-"} | {visitedStr} |\n");
        }
        sb.Append("\n");

        // Section 5: Persona Jaccard Similarity Matrix
        sb.Append("## 5. Persona Jaccard Similarity Matrix (5x5)\n\n");
        sb.Append("authoritative definition: `J(A, B) = |VisitedPoiIds(A) ∩ VisitedPoiIds(B)| / |VisitedPoiIds(A) ∪ VisitedPoiIds(B)|`\n\n");

        sb.Append("| Persona | " + string.Join(" | ", result.JaccardMatrix.PersonaIds) + " |\n");
        sb.Append("|---|:" + string.Join(":|:", Enumerable.Repeat("---", result.JaccardMatrix.PersonaIds.Count)) + ":|\n");
        for (var i = 0; i < result.JaccardMatrix.PersonaIds.Count; i++)
        {
            sb.Append($"| `{result.JaccardMatrix.PersonaIds[i]}` | ");
            for (var j = 0; j < result.JaccardMatrix.PersonaIds.Count; j++)
            {
                sb.Append(result.JaccardMatrix.Matrix[i, j].ToString("0.00", Inv));
                if (j < result.JaccardMatrix.PersonaIds.Count - 1)
                {
                    sb.Append(" | ");
                }
            }
            sb.Append(" |\n");
        }
        sb.Append("\n");

        // Section 6: Detailed Pairwise Jaccard Breakdown
        sb.Append("## 6. Pairwise Jaccard Breakdown & Threshold Analysis\n\n");
        sb.Append($"Threshold: `Jaccard < {thresholdText}` is considered strong personalization differentiation.\n\n");
        sb.Append($"| Persona A | Persona B | Stops A | Stops B | Intersection | Union | Jaccard | Personalized Gate (< {thresholdText}) | Route A vs Route B |\n");
        sb.Append("|---|---|---:|---:|---:|---:|---:|---|---|\n");
        foreach (var pair in result.JaccardMatrix.Pairs)
        {
            var isColdStartComparison = coldStartPairs.Contains(pair);
            var gateStr = isColdStartComparison
                ? "OBSERVED (not gated)"
                : pair.Jaccard < threshold ? "PASS" : $"FAIL (>= {thresholdText})";
            sb.Append($"| `{pair.PersonaIdA}` | `{pair.PersonaIdB}` | {pair.StopCountA} | {pair.StopCountB} | {pair.IntersectionCount} | {pair.UnionCount} | **{pair.Jaccard.ToString("0.0000", Inv)}** | {gateStr} | `[{string.Join(",", pair.VisitedPoiIdsA)}]` vs `[{string.Join(",", pair.VisitedPoiIdsB)}]` |\n");
        }
        sb.Append("\n");

        sb.Append($"### 6.1 Pairs Meeting Personalization Gate (J < {thresholdText})\n\n");
        if (personalizedPairsBelowThreshold.Count == 0)
        {
            sb.Append("None.\n\n");
        }
        else
        {
            foreach (var p in personalizedPairsBelowThreshold)
            {
                sb.Append($"- **`{p.PersonaIdA}` vs `{p.PersonaIdB}`**: Jaccard = **{p.Jaccard.ToString("0.0000", Inv)}** (Intersection: {p.IntersectionCount}, Union: {p.UnionCount}). High divergence in POI selections.\n");
            }
            sb.Append("\n");
        }

        sb.Append($"### 6.2 Pairs Exceeding Threshold (J >= {thresholdText}) & Structural Analysis\n\n");
        if (personalizedPairsAboveOrEqualThreshold.Count == 0)
        {
            sb.Append($"None. All personalized persona pairs met the gate (< {thresholdText}).\n\n");
        }
        else
        {
            foreach (var p in personalizedPairsAboveOrEqualThreshold)
            {
                sb.Append($"- **`{p.PersonaIdA}` vs `{p.PersonaIdB}`**: Jaccard = **{p.Jaccard.ToString("0.0000", Inv)}** (Intersection: {p.IntersectionCount}, Union: {p.UnionCount})\n");
                if (p.Jaccard >= 0.9999)
                {
                    sb.Append($"  - **Observed Structure:** **Identical POI selection (J = 1.00)**. Both personas selected the exact same {p.StopCountA} POIs (`[{string.Join(",", p.VisitedPoiIdsA)}]`).\n");
                }
                else if (p.IntersectionCount == Math.Min(p.StopCountA, p.StopCountB))
                {
                    var smallerPersona = p.StopCountA <= p.StopCountB ? p.PersonaIdA : p.PersonaIdB;
                    var largerPersona = p.StopCountA <= p.StopCountB ? p.PersonaIdB : p.PersonaIdA;
                    sb.Append($"  - **Observed Structure:** **Subset containment**. The {Math.Min(p.StopCountA, p.StopCountB)}-stop itinerary of `{smallerPersona}` is a subset of the {Math.Max(p.StopCountA, p.StopCountB)}-stop itinerary of `{largerPersona}`.\n");
                }
                else
                {
                    sb.Append($"  - **Observed Structure:** **Partial overlap**. Personas share {p.IntersectionCount} stops and differ on {p.UnionCount - p.IntersectionCount} stops.\n");
                }
            }
            sb.Append("\n");
        }

        sb.Append("### 6.3 Cold-Start Comparisons (Observational Only)\n\n");
        sb.Append("Cold start has no preference tokens, so its pairwise Jaccard values are reported but do not determine the personalization gate.\n\n");
        foreach (var pair in coldStartPairs)
        {
            sb.Append($"- **`{pair.PersonaIdA}` vs `{pair.PersonaIdB}`**: Jaccard = **{pair.Jaccard.ToString("0.0000", Inv)}**.\n");
        }
        sb.Append("\n");

        // Section 7: Ablation Study
        sb.Append("## 7. Ablation Study (One-Factor-at-a-Time)\n\n");
        sb.Append("Each ablation modifies exactly one component while keeping all other parameters at baseline:\n\n");
        sb.Append("| Variant | Persona | Feasible | Stops | Travel (min) | Groups | Midday | Objective | Jaccard (vs Full) | Added POIs | Removed POIs | Notes |\n");
        sb.Append("|---|---|---|---:|---:|---:|---:|---:|---:|---|---|---|\n");
        foreach (var a in result.Ablations)
        {
            var addedStr = a.AddedPoiIds.Count > 0 ? string.Join(",", a.AddedPoiIds) : "-";
            var removedStr = a.RemovedPoiIds.Count > 0 ? string.Join(",", a.RemovedPoiIds) : "-";
            sb.Append($"| {a.VariantName} | `{a.PersonaId}` | {(a.Feasible ? "Yes" : "No")} | {a.StopCount} | {a.TotalMatrixTravelMinutes} | {a.DiversityGroupCount} | {a.MiddayOutdoorVisitCount} | {a.ModelObjective?.ToString(Inv) ?? "-"} | {a.JaccardWithFull.ToString("0.00", Inv)} | {addedStr} | {removedStr} | {a.Notes} |\n");
        }
        sb.Append("\n");

        // Section 8: Sensitivity Analysis
        sb.Append("## 8. Sensitivity Analysis (One-at-a-Time Parameter Probes)\n\n");

        var paramGroups = result.Sensitivities.GroupBy(s => s.ParameterName);
        foreach (var group in paramGroups)
        {
            sb.Append($"### 8.{group.Key} Probes\n\n");
            sb.Append("| Parameter | Level | Value | Persona | Feasible | Stops | Travel (min) | Groups | Midday | Objective | Jaccard (vs Base) | Notes |\n");
            sb.Append("|---|---|---:|---|---|---:|---:|---:|---:|---:|---:|---|\n");
            foreach (var s in group)
            {
                sb.Append($"| `{s.ParameterName}` | {s.Level} | {s.Value.ToString("0.000", Inv)} | `{s.PersonaId}` | {(s.Feasible ? "Yes" : "No")} | {s.StopCount} | {s.TotalMatrixTravelMinutes} | {s.DiversityGroupCount} | {s.MiddayOutdoorVisitCount} | {s.ModelObjective?.ToString(Inv) ?? "-"} | {s.JaccardWithBase.ToString("0.00", Inv)} | {s.Notes} |\n");
            }
            sb.Append("\n");
        }

        // Section 9: Findings & Observations
        sb.Append("## 9. Key Findings, Invariants & Limitations\n\n");
        sb.Append("### 9.1 Observed Findings\n");

        if (result.JaccardMatrix.Pairs.Count > 0)
        {
            var lowestPair = result.JaccardMatrix.Pairs.OrderBy(p => p.Jaccard).First();
            var highestPair = result.JaccardMatrix.Pairs.OrderByDescending(p => p.Jaccard).First();
            var lowestGateStr = lowestPair.Jaccard < threshold ? "PASS" : $"FAIL (>= {thresholdText})";
            sb.Append($"1. **Persona Divergence Range**: Minimum Jaccard similarity is **{lowestPair.Jaccard.ToString("0.0000", Inv)}** between `{lowestPair.PersonaIdA}` and `{lowestPair.PersonaIdB}` (Gate: {lowestGateStr}); maximum similarity is **{highestPair.Jaccard.ToString("0.0000", Inv)}** between `{highestPair.PersonaIdA}` and `{highestPair.PersonaIdB}`.\n");
        }

        var noDecay = result.Ablations.Where(value => value.VariantName == "No category decay").ToArray();
        var noDecayChanged = noDecay.Count(value => value.JaccardWithFull < 1.0);
        var findingNumber = result.JaccardMatrix.Pairs.Count > 0 ? 2 : 1;
        sb.Append($"{findingNumber}. **Category Decay Effect (Gamma)**: No category decay changed POI selection for {noDecayChanged}/{noDecay.Length} personas.\n\n");

        sb.Append("### 9.2 Limitations & Honest Declarations\n");
        sb.Append("- **Behavior-signal ablation**: Behavior-signal ablation was not measured because the benchmark corpus contains no seeded historical interaction events.\n");
        sb.Append($"- **Weight calibration**: Base-score component weights favor explicit category/tag preferences while retaining behavior, scenic, and photo signals. No POI- or persona-specific boosts are used to force Jaccard below {thresholdText}.\n");
        sb.Append($"- **Cold-start provenance**: `DefaultGeneralist` uses the production cold-start fallback score {defaultFallback}; it is not a raw BGE-M3 response.\n");
        sb.Append($"- **Corpus size limitation**: Scenario `{result.CityData.Id}` contains {result.CityData.Pois.Count} POIs with mandatory POI constraints bounding minimum intersection.");

        return sb.ToString().TrimEnd('\r', '\n');
    }
}