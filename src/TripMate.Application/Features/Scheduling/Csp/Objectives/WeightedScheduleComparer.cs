using System.Runtime.CompilerServices;
using TripMate.Application.Features.Scheduling.Common;
using TripMate.Application.Features.Scheduling.Routing;

namespace TripMate.Application.Features.Scheduling.Csp.Objectives;

/// <summary>
/// Compares evaluated itinerary schedules (EvaluatedItinerarySchedule) according to the personalized objective function.
/// Uses ConditionalWeakTable to cache computed J objective scores, avoiding recomputations
/// during sorting algorithms without causing memory leaks or repeated allocations.
/// </summary>
internal sealed class WeightedScheduleComparer(
    WeightedObjectiveInputs inputs,
    RoutingContext ctx,
    IReadOnlyDictionary<long, int> nodeOf,
    IReadOnlyDictionary<long, int> matrixIndexOf,
    int[] groupScratch,
    List<GeneratedItineraryItem> itemScratch) : IComparer<EvaluatedItinerarySchedule>
{
    private readonly ConditionalWeakTable<EvaluatedItinerarySchedule, StrongBox<long>> _cache = new();

    public int Compare(EvaluatedItinerarySchedule? x, EvaluatedItinerarySchedule? y)
    {
        if (ReferenceEquals(x, y))
        {
            return 0;
        }

        if (x is null)
        {
            return 1;
        }

        if (y is null)
        {
            return -1;
        }

        var jX = GetObjective(x);
        var jY = GetObjective(y);

        // Tier 1: Higher personalized utility objective ranks first (returns negative)
        var cmp = jY.CompareTo(jX);
        if (cmp != 0)
        {
            return cmp;
        }

        // Tier 2: Shorter total travel minutes ranks first
        cmp = x.TotalMatrixTravelMinutes.CompareTo(y.TotalMatrixTravelMinutes);
        if (cmp != 0)
        {
            return cmp;
        }

        // Tier 3: Earlier end time ranks first
        cmp = x.Plan.EndAtUtc.CompareTo(y.Plan.EndAtUtc);
        if (cmp != 0)
        {
            return cmp;
        }

        // Tier 4: Compare POI ID sequence for deterministic total ordering
        var minCount = Math.Min(x.VisitPoiIds.Count, y.VisitPoiIds.Count);
        for (var i = 0; i < minCount; i++)
        {
            var idCmp = x.VisitPoiIds[i].CompareTo(y.VisitPoiIds[i]);
            if (idCmp != 0)
            {
                return idCmp;
            }
        }

        return x.VisitPoiIds.Count.CompareTo(y.VisitPoiIds.Count);
    }

    public long GetObjective(EvaluatedItinerarySchedule s)
    {
        if (_cache.TryGetValue(s, out var boxed))
        {
            return boxed.Value;
        }

        var j = ObjectiveCalculator.Evaluate(inputs, ctx, s, nodeOf, matrixIndexOf, groupScratch, itemScratch);
        _cache.Add(s, new StrongBox<long>(j));
        return j;
    }
}