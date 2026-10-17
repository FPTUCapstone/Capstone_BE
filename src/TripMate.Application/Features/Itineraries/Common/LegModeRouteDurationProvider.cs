using TripMate.Application.Features.Scheduling.Common;
using TripMate.Domain.Enums;

namespace TripMate.Application.Features.Itineraries.Common;

/// <summary>
/// Builds the trip-mode matrix, then replaces the legs the traveler chose another transport for with the
/// matching cell of that mode's matrix (one extra routing call per distinct mode). Everything downstream —
/// scheduling, validation and the persisted leg — therefore uses the chosen mode for those legs.
/// </summary>
public sealed class LegModeRouteDurationProvider(
    IRouteDurationProvider inner,
    IReadOnlyCollection<LegModeOverride> overrides,
    IReadOnlyCollection<RoutePoint>? stopLocations = null) : IRouteDurationProvider
{
    public async Task<RouteDurationMatrix> GetMatrixAsync(
        IReadOnlyList<RoutePoint> points,
        TransportMode transportMode,
        CancellationToken cancellationToken)
    {
        var baseMatrix = await inner.GetMatrixAsync(points, transportMode, cancellationToken);
        var effective = overrides.Where(leg => leg.Mode != transportMode).ToArray();
        if (effective.Length == 0)
        {
            return baseMatrix;
        }

        var size = points.Count;
        var minutes = new int[size, size];
        var meters = new int[size, size];
        var unreachable = new bool[size, size];
        var hasMeters = true;
        for (var row = 0; row < size; row++)
        {
            for (var column = 0; column < size; column++)
            {
                Copy(baseMatrix, row, column, minutes, meters, unreachable, ref hasMeters);
            }
        }

        foreach (var group in effective.GroupBy(leg => leg.Mode))
        {
            var modeMatrix = await inner.GetMatrixAsync(points, group.Key, cancellationToken);
            foreach (var leg in group)
            {
                var from = IndexOf(points, leg.From);
                var to = IndexOf(points, leg.To);
                if (from < 0 || to < 0)
                {
                    continue;
                }

                Copy(modeMatrix, from, to, minutes, meters, unreachable, ref hasMeters);

                // A rest the solver places between the two stops splits the leg; both halves keep the chosen mode.
                foreach (var via in IntermediateIndexes(points, from, to))
                {
                    Copy(modeMatrix, from, via, minutes, meters, unreachable, ref hasMeters);
                    Copy(modeMatrix, via, to, minutes, meters, unreachable, ref hasMeters);
                }
            }
        }

        return RouteDurationMatrix.Create(minutes, unreachable, hasMeters ? meters : null);
    }

    private static void Copy(
        RouteDurationMatrix source,
        int row,
        int column,
        int[,] minutes,
        int[,] meters,
        bool[,] unreachable,
        ref bool hasMeters)
    {
        if (!source.IsReachable(row, column))
        {
            unreachable[row, column] = true;
            return;
        }

        unreachable[row, column] = false;
        minutes[row, column] = source.GetMinutes(row, column);
        if (source.GetMeters(row, column) is { } distance)
        {
            meters[row, column] = distance;
        }
        else if (row != column)
        {
            hasMeters = false;
        }
    }

    /// <summary>Points that are not stops of the itinerary (possible rest places) and so may sit inside a leg.</summary>
    private IEnumerable<int> IntermediateIndexes(IReadOnlyList<RoutePoint> points, int from, int to)
    {
        if (stopLocations is null)
        {
            yield break;
        }

        for (var index = 0; index < points.Count; index++)
        {
            if (index != from && index != to && !stopLocations.Contains(points[index]))
            {
                yield return index;
            }
        }
    }

    private static int IndexOf(IReadOnlyList<RoutePoint> points, RoutePoint point)
    {
        for (var index = 0; index < points.Count; index++)
        {
            if (points[index] == point)
            {
                return index;
            }
        }

        return -1;
    }
}

/// <summary>The transport chosen for the leg from one stop location to the next.</summary>
public sealed record LegModeOverride(RoutePoint From, RoutePoint To, TransportMode Mode);

/// <summary>Remembers matrices so repeated evaluations of the same itinerary call the routing service once.</summary>
public sealed class MemoizingRouteDurationProvider(IRouteDurationProvider inner) : IRouteDurationProvider
{
    private readonly Dictionary<string, RouteDurationMatrix> _matrices = [];

    public async Task<RouteDurationMatrix> GetMatrixAsync(
        IReadOnlyList<RoutePoint> points,
        TransportMode transportMode,
        CancellationToken cancellationToken)
    {
        var key = transportMode + "|" + string.Join(';', points.Select(point => $"{point.Latitude},{point.Longitude}"));
        if (!_matrices.TryGetValue(key, out var matrix))
        {
            matrix = await inner.GetMatrixAsync(points, transportMode, cancellationToken);
            _matrices[key] = matrix;
        }

        return matrix;
    }
}