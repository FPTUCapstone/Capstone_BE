using Microsoft.EntityFrameworkCore;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Features.Scheduling.Common;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Infrastructure.Routing;

/// <summary>
/// Uses Staff-maintained Active route metrics for known POI pairs, keeping Mapbox as the
/// provider for other legs and for turn-by-turn geometry. The solver contract is unchanged.
/// </summary>
public sealed class CatalogRouteDurationProvider(
    IApplicationDbContext dbContext,
    IRouteDurationProvider fallback) : IRouteDurationProvider
{
    public bool SupportsTransportMode(TransportMode transportMode) => Enum.IsDefined(transportMode);

    public async Task<RouteDurationMatrix> GetMatrixAsync(IReadOnlyList<RoutePoint> points,
        TransportMode transportMode, CancellationToken cancellationToken)
    {
        if (points.Count < 2)
        {
            return await fallback.GetMatrixAsync(points, transportMode, cancellationToken);
        }

        var latitudes = points.Select(point => point.Latitude).Distinct().ToArray();
        var longitudes = points.Select(point => point.Longitude).Distinct().ToArray();
        var locations = await dbContext.PointsOfInterest.AsNoTracking()
            .Where(poi => poi.Status == PointOfInterestStatus.Active
                && latitudes.Contains(poi.Latitude) && longitudes.Contains(poi.Longitude))
            .Select(poi => new { poi.Id, poi.Latitude, poi.Longitude }).ToListAsync(cancellationToken);
        var locationsById = locations.ToDictionary(poi => poi.Id);
        var unambiguous = locations.GroupBy(poi => (poi.Latitude, poi.Longitude))
            .Where(group => group.Count() == 1).ToDictionary(group => group.Key, group => group.Single().Id);
        var poiIds = points.Select(point => point.PoiId is { } id
            ? locationsById.TryGetValue(id, out var poi)
                && poi.Latitude == point.Latitude && poi.Longitude == point.Longitude ? (long?)id : null
            : unambiguous.TryGetValue((point.Latitude, point.Longitude), out var matchedId) ? (long?)matchedId : null)
            .ToArray();
        var requestedPoiIds = poiIds.OfType<long>().Distinct().ToArray();
        var routes = await dbContext.RouteSegments.AsNoTracking()
            .Where(route => route.Status == RouteStatus.Active && route.TransportMode == transportMode
                && route.FromPoi.Status == PointOfInterestStatus.Active
                && route.ToPoi.Status == PointOfInterestStatus.Active
                && requestedPoiIds.Contains(route.FromPoiId) && requestedPoiIds.Contains(route.ToPoiId))
            .Select(route => new
            {
                route.FromPoiId,
                route.ToPoiId,
                route.EstimatedDurationMinutes,
                route.DistanceKm,
            }).ToListAsync(cancellationToken);

        // Prefer known POI identity. A free GPS location may identify a POI only when its
        // coordinates are unambiguous; it must not inherit an arbitrary colocated POI's route.
        var edges = routes.GroupBy(route => (route.FromPoiId, route.ToPoiId))
            .Where(group => group.Count() == 1)
            .ToDictionary(group => group.Key, group => group.Single());
        var size = points.Count;
        var complete = true;
        for (var row = 0; row < size && complete; row++)
        {
            for (var col = 0; col < size; col++)
            {
                if (!IsSameLocation(points[row], points[col])
                    && (poiIds[row] is not { } from || poiIds[col] is not { } to || !edges.ContainsKey((from, to))))
                {
                    complete = false;
                    break;
                }
            }
        }

        if (!complete && !fallback.SupportsTransportMode(transportMode))
        {
            throw new RouteDurationProviderException(nameof(CatalogRouteDurationProvider),
                RouteDurationProviderFailureKind.Unavailable,
                "The stored route network does not cover all required legs for this transport mode.",
                new NotSupportedException("The map provider does not supply this transport mode's missing metrics."));
        }
        var original = complete ? null : await fallback.GetMatrixAsync(points, transportMode, cancellationToken);
        var minutes = new int[size, size];
        var meters = new int[size, size];
        var unreachable = new bool[size, size];
        var hasDistances = true;
        for (var row = 0; row < size; row++)
        {
            for (var col = 0; col < size; col++)
            {
                // Start/end and a POI candidate may be the same location at different matrix indexes.
                if (IsSameLocation(points[row], points[col]))
                {
                    continue;
                }
                if (poiIds[row] is { } from && poiIds[col] is { } to && edges.TryGetValue((from, to), out var edge))
                {
                    // The planner cannot schedule a leg longer than its maximum supported horizon.
                    // Bound its matrix input before buffer arithmetic. Keep the stored metric unchanged.
                    minutes[row, col] = Math.Min(edge.EstimatedDurationMinutes, SchedulingRequest.MaxAvailableMinutes + 1);
                    meters[row, col] = checked((int)(edge.DistanceKm * 1000m));
                }
                else
                {
                    unreachable[row, col] = !original!.IsReachable(row, col);
                    if (!unreachable[row, col])
                    {
                        minutes[row, col] = original.GetMinutes(row, col);
                        if (original.GetMeters(row, col) is { } distance)
                        {
                            meters[row, col] = distance;
                        }
                        else
                        {
                            hasDistances = false;
                        }
                    }
                }
            }
        }
        return RouteDurationMatrix.Create(minutes, unreachable, hasDistances ? meters : null);
    }

    private static bool IsSameLocation(RoutePoint from, RoutePoint to) =>
        from.Latitude == to.Latitude && from.Longitude == to.Longitude
        && (!from.PoiId.HasValue || !to.PoiId.HasValue || from.PoiId == to.PoiId);
}