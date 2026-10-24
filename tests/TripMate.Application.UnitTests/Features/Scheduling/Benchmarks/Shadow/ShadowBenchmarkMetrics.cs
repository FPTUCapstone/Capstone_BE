using TripMate.Application.Features.Scheduling.Common;
using TripMate.Application.Features.Scheduling.Csp;
using TripMate.Application.UnitTests.Features.Scheduling.Fixtures;
using TripMate.Domain.Constants;
using TripMate.Domain.Enums;

namespace TripMate.Application.UnitTests.Features.Scheduling.Benchmarks.Shadow;

public static class ShadowBenchmarkMetrics
{
    private static readonly TimeOnly MiddayStart = new(11, 0);
    private static readonly TimeOnly MiddayEnd = new(14, 30);

    public static ShadowBenchmarkRunResult Create(
        string scenarioId,
        string personaId,
        ObjectiveMode mode,
        EvaluatedItinerarySchedule schedule,
        IReadOnlyDictionary<long, GenerationCandidate> candidates,
        TimeZoneInfo timeZone,
        IReadOnlyList<double> durationSamplesMs)
    {
        var visits = schedule.Plan.Items
            .Where(item => item.Kind == ItineraryItemKind.Visit && item.PointOfInterestId.HasValue)
            .OrderBy(item => item.SequenceNo)
            .ToArray();

        var diversityGroupCount = visits
            .Select(item => candidates[item.PointOfInterestId!.Value])
            .Select(candidate => PoiCategoryNames.GetDiversityGroup(candidate.CategoryName))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();

        var middayOutdoorVisitCount = visits.Count(item =>
        {
            var candidate = candidates[item.PointOfInterestId!.Value];
            return candidate.IndoorOutdoor == IndoorOutdoorType.Outdoor
                && !candidate.HasShelter
                && OverlapsMidday(item, timeZone);
        });
        var middayOutdoorVisitRate = visits.Length == 0
            ? 0
            : (double)middayOutdoorVisitCount / visits.Length * 100;

        return new ShadowBenchmarkRunResult(
            scenarioId,
            personaId,
            mode,
            Feasible: true,
            visits.Length,
            schedule.TotalMatrixTravelMinutes,
            diversityGroupCount,
            middayOutdoorVisitCount,
            visits.Length,
            middayOutdoorVisitRate,
            schedule.Plan.ModelObjective,
            schedule.VisitPoiIds.ToArray(),
            durationSamplesMs.ToArray(),
            Percentile(durationSamplesMs, 0.50),
            Percentile(durationSamplesMs, 0.95));
    }

    public static double Percentile(IEnumerable<double> samples, double percentile) =>
        SolverComparisonReport.Percentile(samples, percentile);

    private static bool OverlapsMidday(GeneratedItineraryItem item, TimeZoneInfo timeZone)
    {
        var arrival = TimeOnly.FromDateTime(TimeZoneInfo.ConvertTime(item.PlannedArrivalUtc, timeZone).DateTime);
        var departure = TimeOnly.FromDateTime(TimeZoneInfo.ConvertTime(item.PlannedDepartureUtc, timeZone).DateTime);
        return arrival < MiddayEnd && departure > MiddayStart;
    }
}