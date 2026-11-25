using TripMate.Application.Features.Itineraries.Common;
using TripMate.Application.Features.Rerouting.Common;
using TripMate.Domain.Entities;

namespace TripMate.Application.Features.Rerouting.Engine;

/// <summary>
/// One candidate plan for the remaining stops: the ordered POIs, which POIs it removes, and the strategy label
/// stored in the snapshot. <paramref name="IsBaseProbe"/> marks the current order, which is only evaluated to find
/// out whether the plan already works (then there is nothing to propose).
/// </summary>
internal sealed record ReroutingVariant(
    string Strategy,
    IReadOnlyList<ItineraryStop> Stops,
    IReadOnlyList<long> RemovedPoiIds,
    bool IsBaseProbe = false);

/// <summary>What a strategy may look at (spec 4.2/4.3): the Pending stops, the affected one, the anchor and bounds.</summary>
internal sealed record ReroutingStrategyContext(
    IReadOnlyList<TripSessionItem> Pending,
    TripSessionItem Affected,
    PointOfInterest AffectedPoi,
    EvaluationAnchor Anchor,
    int MaxVariants,
    int MaxReplacementsPerStop,
    IReadOnlySet<string> PreferenceTokens,
    IReadOnlyDictionary<long, int> PreferenceScoreByPoiId,
    ISet<long> ExcludedPoiIds,
    ReroutingCandidateFinder Candidates);

/// <summary>
/// Produces batches of variants for one incident type, in priority order. The builder evaluates a batch at a time
/// and keeps the first batch that has a feasible variant; every batch holds variants of a single strategy.
/// </summary>
internal interface IReroutingStrategy
{
    string IncidentType { get; }

    Task<IReadOnlyList<IReadOnlyList<ReroutingVariant>>> CreateBatchesAsync(
        ReroutingStrategyContext context,
        CancellationToken cancellationToken);
}

internal static class ReroutingVariants
{
    public static IReadOnlyList<ItineraryStop> Stops(IEnumerable<long> poiIds) =>
        poiIds.Select(poiId => new ItineraryStop(poiId)).ToArray();

    public static long[] PendingPoiIds(ReroutingStrategyContext context) =>
        context.Pending.Select(item => item.PoiId).ToArray();

    /// <summary>The current order with <paramref name="poiId"/> replaced in place by <paramref name="replacementPoiId"/>.</summary>
    public static IReadOnlyList<ItineraryStop> Replace(ReroutingStrategyContext context, long poiId, long replacementPoiId) =>
        Stops(PendingPoiIds(context).Select(id => id == poiId ? replacementPoiId : id));

    public static IReadOnlyList<ItineraryStop> Without(ReroutingStrategyContext context, params long[] removed) =>
        Stops(PendingPoiIds(context).Where(id => !removed.Contains(id)));

    /// <summary>
    /// The pending stops the Traveler could give up (never a mandatory one), least preferred first, ties in
    /// itinerary order. Used by every "drop" and "reduce" variant.
    /// </summary>
    public static IReadOnlyList<TripSessionItem> Droppable(ReroutingStrategyContext context) =>
        context.Pending
            .Where(item => !item.IsMandatory)
            .OrderBy(item => context.PreferenceScoreByPoiId.GetValueOrDefault(item.PoiId))
            .ThenBy(item => item.SequenceNo)
            .ToArray();

    /// <summary>
    /// Spec 4.3 <c>ReduceStops</c>: first the current order as a probe (if it already fits there is nothing to fix),
    /// then each single drop; a second batch of pairs is only needed when no single drop fits.
    /// </summary>
    public static IReadOnlyList<IReadOnlyList<ReroutingVariant>> ReduceStopsBatches(ReroutingStrategyContext context)
    {
        var droppable = Droppable(context);
        var batches = new List<IReadOnlyList<ReroutingVariant>>();
        var singles = new List<ReroutingVariant>
        {
            new(ReroutingStrategies.ReduceStops, Stops(PendingPoiIds(context)), [], IsBaseProbe: true),
        };
        if (context.Pending.Count > 1)
        {
            singles.AddRange(droppable
                .Take(Math.Max(0, context.MaxVariants - 1))
                .Select(item => new ReroutingVariant(
                    ReroutingStrategies.ReduceStops, Without(context, item.PoiId), [item.PoiId])));
        }

        batches.Add(singles);

        // Pairs from the least preferred few, so the batch stays small (C(4,2) = 6).
        if (context.Pending.Count > 2)
        {
            var pool = droppable.Take(4).ToArray();
            var pairs = new List<ReroutingVariant>();
            for (var first = 0; first < pool.Length; first++)
            {
                for (var second = first + 1; second < pool.Length; second++)
                {
                    pairs.Add(new ReroutingVariant(
                        ReroutingStrategies.ReduceStops,
                        Without(context, pool[first].PoiId, pool[second].PoiId),
                        [pool[first].PoiId, pool[second].PoiId]));
                }
            }

            if (pairs.Count > 0)
            {
                batches.Add(pairs.Take(context.MaxVariants).ToArray());
            }
        }

        return batches;
    }
}