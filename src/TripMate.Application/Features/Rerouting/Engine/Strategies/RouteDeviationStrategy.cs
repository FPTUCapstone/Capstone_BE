using TripMate.Application.Common.Geo;
using TripMate.Application.Features.Rerouting.Common;
using TripMate.Domain.Entities;

namespace TripMate.Application.Features.Rerouting.Engine.Strategies;

/// <summary>
/// Spec 4.3 <c>RouteDeviation</c>: <c>Reorder</c> the Pending stops from the anchor (all permutations of up to six
/// stops, otherwise nearest neighbour; every stop is kept, mandatory ones included) but only into orders strictly
/// shorter in straight-line length than the current one, else <c>ReduceStops</c>. The Backend has no GPS, so the anchor
/// is the last reached stop (spec decision D3).
/// </summary>
internal sealed class RouteDeviationStrategy : IReroutingStrategy
{
    private const int MaxPermutedStops = 6;

    public string IncidentType => Incident.RouteDeviationType;

    public Task<IReadOnlyList<IReadOnlyList<ReroutingVariant>>> CreateBatchesAsync(
        ReroutingStrategyContext context,
        CancellationToken cancellationToken)
    {
        var batches = new List<IReadOnlyList<ReroutingVariant>>();
        var currentLength = Length(context.Pending, context);
        var reorders = (context.Pending.Count <= MaxPermutedStops ? Permutations(context) : [NearestNeighbour(context)])
            .Where(candidate => candidate.Length < currentLength)
            .OrderBy(candidate => candidate.Length)
            .ThenBy(candidate => string.Join(',', candidate.Order))
            .Take(context.MaxVariants)
            .Select(candidate => new ReroutingVariant(
                ReroutingStrategies.Reorder, ReroutingVariants.Stops(candidate.Order), []))
            .ToArray();
        if (reorders.Length > 0)
        {
            batches.Add(reorders);
        }

        batches.AddRange(ReroutingVariants.ReduceStopsBatches(context));
        return Task.FromResult<IReadOnlyList<IReadOnlyList<ReroutingVariant>>>(batches);
    }

    private static List<(long[] Order, decimal Length)> Permutations(ReroutingStrategyContext context)
    {
        var results = new List<(long[] Order, decimal Length)>();
        Permute(context.Pending.ToArray(), 0, results, context);
        return results;
    }

    private static void Permute(
        TripSessionItem[] stops,
        int index,
        List<(long[] Order, decimal Length)> results,
        ReroutingStrategyContext context)
    {
        if (index == stops.Length)
        {
            results.Add((stops.Select(stop => stop.PoiId).ToArray(), Length(stops, context)));
            return;
        }

        for (var swap = index; swap < stops.Length; swap++)
        {
            (stops[index], stops[swap]) = (stops[swap], stops[index]);
            Permute(stops, index + 1, results, context);
            (stops[index], stops[swap]) = (stops[swap], stops[index]);
        }
    }

    private static (long[] Order, decimal Length) NearestNeighbour(ReroutingStrategyContext context)
    {
        var remaining = context.Pending.ToList();
        var ordered = new List<TripSessionItem>();
        var (latitude, longitude) = (context.Anchor.Start.Latitude, context.Anchor.Start.Longitude);
        while (remaining.Count > 0)
        {
            var next = remaining
                .OrderBy(stop => GeoDistance.EquirectangularKilometers(latitude, longitude, stop.Latitude, stop.Longitude))
                .ThenBy(stop => stop.SequenceNo)
                .First();
            ordered.Add(next);
            remaining.Remove(next);
            (latitude, longitude) = (next.Latitude, next.Longitude);
        }

        return (ordered.Select(stop => stop.PoiId).ToArray(), Length(ordered, context));
    }

    private static decimal Length(IEnumerable<TripSessionItem> stops, ReroutingStrategyContext context)
    {
        var (latitude, longitude) = (context.Anchor.Start.Latitude, context.Anchor.Start.Longitude);
        var total = 0m;
        foreach (var stop in stops)
        {
            total += GeoDistance.EquirectangularKilometers(latitude, longitude, stop.Latitude, stop.Longitude);
            (latitude, longitude) = (stop.Latitude, stop.Longitude);
        }

        return total;
    }
}