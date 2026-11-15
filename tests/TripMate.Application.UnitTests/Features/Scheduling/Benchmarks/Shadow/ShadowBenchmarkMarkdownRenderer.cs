using System.Globalization;
using System.Text;

namespace TripMate.Application.UnitTests.Features.Scheduling.Benchmarks.Shadow;

public static class ShadowBenchmarkMarkdownRenderer
{
    public static string Render(
        ShadowBenchmarkReportMetadata metadata,
        IEnumerable<ShadowBenchmarkRunResult> results,
        IEnumerable<ShadowBenchmarkSeedComparison>? seedComparisons = null)
    {
        var ordered = results
            .OrderBy(result => result.ScenarioId, StringComparer.Ordinal)
            .ThenBy(result => result.PersonaId, StringComparer.Ordinal)
            .ThenBy(result => result.Mode)
            .ToArray();
        var text = new StringBuilder();

        var scenariosCount = ordered.Select(result => result.ScenarioId).Distinct(StringComparer.Ordinal).Count();
        var personasCount = ordered.Select(result => result.PersonaId).Distinct(StringComparer.Ordinal).Count();
        var modesCount = ordered.Select(result => result.Mode).Distinct().Count();
        var totalCases = ordered.Length;
        var failedCases = ordered.Count(result => !result.Feasible);

        text.Append("# Shadow Benchmark v3b\n\n");
        text.Append(Invariant($"- Run date: {metadata.RunDate}\n"));
        text.Append(Invariant($"- Build: {metadata.BuildConfiguration}\n"));
        text.Append(Invariant($"- OS: {metadata.OperatingSystem}\n"));
        text.Append(Invariant($"- Runtime: {metadata.Runtime}\n"));
        text.Append(Invariant($"- Machine: {metadata.Machine}\n"));
        text.Append(Invariant($"- Logical processors: {metadata.LogicalProcessorCount}\n"));
        text.Append(Invariant($"- Warm-up iterations: {metadata.WarmupIterations}\n"));
        text.Append(Invariant($"- Scenarios: {scenariosCount}\n"));
        text.Append(Invariant($"- Personas: {personasCount}\n"));
        text.Append(Invariant($"- Modes: {modesCount}\n"));
        text.Append(Invariant($"- Total cases: {totalCases}\n"));
        text.Append(Invariant($"- Failed cases: {failedCases}\n\n"));
        text.Append("Runtime unit is milliseconds; p50/p95 use sorted sample index floor(n × p), clamped to n - 1.\n\n");
        text.Append("Midday outdoor means an unsheltered outdoor visit overlapping [11:00, 14:30) local time; rate denominator is all visits.\n\n");
        text.Append("Diversity is the count of distinct `PoiCategoryNames.GetDiversityGroup` values.\n\n");
        text.Append("## Aggregate v3b\n\n");
        text.Append("| Mode | Cases | Feasible | Avg stops | Avg total travel | Avg diversity groups | Avg midday rate | Runtime samples | p50 (ms) | p95 (ms) |\n");
        text.Append("|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|\n");
        foreach (var modeGroup in ordered.GroupBy(result => result.Mode).OrderBy(group => group.Key))
        {
            var feasible = modeGroup.Where(result => result.Feasible).ToArray();
            var samples = modeGroup.SelectMany(result => result.DurationSamplesMs).ToArray();
            text.Append(Invariant(
                $"| {modeGroup.Key} | {modeGroup.Count()} | {feasible.Length} | {Average(feasible, result => result.StopCount):F2} | {Average(feasible, result => result.TotalMatrixTravelMinutes):F2} | {Average(feasible, result => result.DiversityGroupCount):F2} | {Average(feasible, result => result.MiddayOutdoorVisitRate):F2}% | {samples.Length} | {ShadowBenchmarkMetrics.Percentile(samples, 0.50):F2} | {ShadowBenchmarkMetrics.Percentile(samples, 0.95):F2} |\n"));
        }

        text.Append("\n## v3a → v3b comparison\n\n");
        text.Append("### Behavior and data\n\n");
        text.Append("- v3b keeps the reviewed 20 Golden scenarios plus one Da Nang scenario, but extends that city fixture from 16 to 19 POIs using three nature records from the approved planning seed. No second coastal POI was invented because the approved seed has none.\n");
        text.Append("- v3a travel omitted the start and return legs; v3b reports authoritative `EvaluatedItinerarySchedule.TotalMatrixTravelMinutes`. v3a counted canonical categories and every midday visit; v3b reports diversity groups and only unsheltered outdoor visits overlapping midday. Those columns are semantic corrections, not like-for-like deltas.\n\n");
        text.Append("The baseline 16-POI outcomes are locked by a deterministic regression test. The table below isolates the effect of adding the three approved nature POIs while using the same execution pipeline and metric definitions on both sides.\n\n");
        text.Append("| Persona | Mode | Stops 16 -> 19 | Travel 16 -> 19 | Groups 16 -> 19 | Midday 16 -> 19 | Objective 16 -> 19 | Route IDs 16 -> 19 |\n");
        text.Append("|---|---|---:|---:|---:|---:|---:|---|\n");
        foreach (var comparison in seedComparisons ?? [])
        {
            text.Append(Invariant(
                $"| {comparison.PersonaId} | {comparison.Mode} | {comparison.BaselineStopCount} -> {comparison.V3bStopCount} | {comparison.BaselineTravelMinutes} -> {comparison.V3bTravelMinutes} | {comparison.BaselineDiversityGroupCount} -> {comparison.V3bDiversityGroupCount} | {comparison.BaselineMiddayOutdoorCount} -> {comparison.V3bMiddayOutdoorCount} | {comparison.BaselineModelObjective?.ToString(CultureInfo.InvariantCulture) ?? "-"} -> {comparison.V3bModelObjective?.ToString(CultureInfo.InvariantCulture) ?? "-"} | {string.Join(',', comparison.BaselineVisitedPoiIds)} -> {string.Join(',', comparison.V3bVisitedPoiIds)} |\n"));
        }

        text.Append('\n');
        text.Append("### Runtime\n\n");
        text.Append("- v3a timed one solve per case and calculated percentiles across different scenarios. v3b performs the declared warm-up and repeated samples for every case, then applies one documented percentile convention. Runtime numbers are therefore not directly comparable.\n");
        text.Append("- This is a local diagnostic benchmark, not an SLA or a production performance claim; OS scheduling, thermal state and background load remain uncontrolled.\n\n");
        text.Append("## Per-case results\n\n");
        text.Append("| Scenario | Persona | Mode | Status | Stops | Total travel (min) | Diversity groups | Midday outdoor | Midday rate | Samples | p50 (ms) | p95 (ms) | Model objective |\n");
        text.Append("|---|---|---|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|\n");

        foreach (var result in ordered)
        {
            text.Append(Invariant(
                $"| {result.ScenarioId} | {result.PersonaId} | {result.Mode} | {(result.Feasible ? "Feasible" : "Failed")} | {result.StopCount} | {result.TotalMatrixTravelMinutes} | {result.DiversityGroupCount} | {result.MiddayOutdoorVisitCount}/{result.MiddayOutdoorVisitDenominator} | {result.MiddayOutdoorVisitRate:F2}% | {result.DurationSamplesMs.Count} | {result.P50Milliseconds:F2} | {result.P95Milliseconds:F2} | {result.ModelObjective?.ToString(CultureInfo.InvariantCulture) ?? "-"} |\n"));
        }

        return text.ToString();
    }

    private static string Invariant(FormattableString value) =>
        value.ToString(CultureInfo.InvariantCulture);

    private static double Average(
        IReadOnlyCollection<ShadowBenchmarkRunResult> results,
        Func<ShadowBenchmarkRunResult, double> selector) =>
        results.Count == 0 ? 0 : results.Average(selector);
}