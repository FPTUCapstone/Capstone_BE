namespace TripMate.Application.UnitTests.Features.Scheduling.Benchmarks.Shadow;

public static class ShadowBenchmarkComparison
{
    public static IReadOnlyList<ShadowBenchmarkSeedComparison> CompareSeedExtension(
        IEnumerable<ShadowBenchmarkRunResult> baselineResults,
        IEnumerable<ShadowBenchmarkRunResult> v3bResults)
    {
        var baseline = baselineResults.ToDictionary(result => (result.PersonaId, result.Mode));
        return v3bResults
            .Where(result => result.ScenarioId == "danang-real-1day")
            .OrderBy(result => result.PersonaId, StringComparer.Ordinal)
            .ThenBy(result => result.Mode)
            .Select(result =>
            {
                var before = baseline[(result.PersonaId, result.Mode)];
                return new ShadowBenchmarkSeedComparison(
                    result.PersonaId,
                    result.Mode,
                    before.StopCount,
                    result.StopCount,
                    before.TotalMatrixTravelMinutes,
                    result.TotalMatrixTravelMinutes,
                    before.DiversityGroupCount,
                    result.DiversityGroupCount,
                    before.MiddayOutdoorVisitCount,
                    result.MiddayOutdoorVisitCount,
                    before.ModelObjective,
                    result.ModelObjective,
                    before.VisitedPoiIds,
                    result.VisitedPoiIds);
            })
            .ToArray();
    }
}