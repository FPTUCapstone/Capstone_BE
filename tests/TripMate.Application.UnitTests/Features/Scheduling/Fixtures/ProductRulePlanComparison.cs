using TripMate.Application.Features.Scheduling.Common;
using TripMate.Domain.Enums;

namespace TripMate.Application.UnitTests.Features.Scheduling.Fixtures;

/// <summary>
/// Applies the product rule of <see cref="ScheduleGlobalComparator"/> to two generated plans of the
/// same input, so solvers can be compared from their public output. A negative result means
/// <paramref name="x"/> is the better itinerary.
/// </summary>
public static class ProductRulePlanComparison
{
    public static int Compare(
        GenerationInput input,
        RouteDurationMatrix matrix,
        GeneratedItineraryPlan x,
        GeneratedItineraryPlan y)
    {
        var indices = CandidateMatrixIndices(input);
        var canonicalOptionals = OptionalRouteOptimizer.SortOptionalsCanonical(
            input.Candidates.ToArray(),
            input.MandatoryPoiIds,
            indices,
            matrix);
        var xVisits = VisitIds(x);
        var yVisits = VisitIds(y);

        // 1. Optional-inclusion vector in canonical rank order.
        foreach (var optional in canonicalOptionals)
        {
            var xHas = xVisits.Contains(optional.Id);
            var yHas = yVisits.Contains(optional.Id);
            if (xHas != yHas)
            {
                return xHas ? -1 : 1;
            }
        }

        // 2. Total matrix travel minutes, then 3. total duration and end time.
        var travel = MatrixTravelMinutes(input, matrix, x).CompareTo(MatrixTravelMinutes(input, matrix, y));
        if (travel != 0)
        {
            return travel;
        }

        var duration = x.TotalDurationMinutes.CompareTo(y.TotalDurationMinutes);
        if (duration != 0)
        {
            return duration;
        }

        var end = x.EndAtUtc.CompareTo(y.EndAtUtc);
        if (end != 0)
        {
            return end;
        }

        // 4. Visit-ID sequence, lexicographically ascending.
        for (var i = 0; i < Math.Min(xVisits.Count, yVisits.Count); i++)
        {
            var id = xVisits[i].CompareTo(yVisits[i]);
            if (id != 0)
            {
                return id;
            }
        }

        return xVisits.Count.CompareTo(yVisits.Count);
    }

    /// <summary>
    /// Matrix travel along every stop with a POI (visits and rest stops), including the final leg
    /// to the end point, matching <c>EvaluatedItinerarySchedule.TotalMatrixTravelMinutes</c>.
    /// </summary>
    public static int MatrixTravelMinutes(GenerationInput input, RouteDurationMatrix matrix, GeneratedItineraryPlan plan)
    {
        var indices = CandidateMatrixIndices(input);
        var previous = 0;
        var total = 0;
        foreach (var item in plan.Items.OrderBy(item => item.SequenceNo))
        {
            if (item.PointOfInterestId is { } poiId)
            {
                var next = indices[poiId];
                total += matrix.GetMinutes(previous, next);
                previous = next;
            }
        }

        return total + matrix.GetMinutes(previous, matrix.PointCount - 1);
    }

    private static List<long> VisitIds(GeneratedItineraryPlan plan) =>
        plan.Items
            .OrderBy(item => item.SequenceNo)
            .Where(item => item.Kind == ItineraryItemKind.Visit && item.PointOfInterestId.HasValue)
            .Select(item => item.PointOfInterestId!.Value)
            .ToList();

    // Matrix layout used by ItineraryGenerationService: 0 = start, candidate i = i + 1, last = end.
    private static Dictionary<long, int> CandidateMatrixIndices(GenerationInput input) =>
        input.Candidates.Select((candidate, index) => (candidate.Id, Index: index + 1))
            .ToDictionary(pair => pair.Id, pair => pair.Index);
}