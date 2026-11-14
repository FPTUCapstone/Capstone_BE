using Microsoft.EntityFrameworkCore;

using TripMate.Application.Common.Interfaces;
using TripMate.Domain.Enums;

namespace TripMate.Application.Features.Navigation.Common;

/// <summary>Current catalog metrics for navigation; never rewrites the itinerary's stored timing.</summary>
public static class ActiveRouteNavigationProjection
{
    public static async Task<NavigationSessionResponse> EnrichAsync(IApplicationDbContext dbContext,
        NavigationSessionResponse response, CancellationToken cancellationToken)
    {
        var items = response.Items.OrderBy(item => item.SequenceNo).ToArray();
        if (items.Length < 2)
        {
            return response;
        }
        var ids = items.Select(item => item.PoiId).Distinct().ToArray();
        var routes = await dbContext.RouteSegments.AsNoTracking()
            .Where(route => route.Status == RouteStatus.Active
                && route.FromPoi.Status == PointOfInterestStatus.Active
                && route.ToPoi.Status == PointOfInterestStatus.Active
                && ids.Contains(route.FromPoiId) && ids.Contains(route.ToPoiId))
            .Select(route => new NavigationRouteLeg(route.Id, route.FromPoiId, route.ToPoiId,
                route.DistanceKm, route.EstimatedDurationMinutes, route.TransportMode))
            .ToListAsync(cancellationToken);
        var legs = new List<NavigationRouteLeg>();
        for (var index = 1; index < items.Length; index++)
        {
            var mode = items[index].TransportModeFromPrevious ?? response.TransportMode;
            var match = routes.SingleOrDefault(route => route.FromPoiId == items[index - 1].PoiId
                && route.ToPoiId == items[index].PoiId && route.TransportMode == mode);
            if (match is not null)
            {
                legs.Add(match);
            }
        }
        return response with { RouteLegs = legs };
    }
}

public sealed record NavigationRouteLeg(long Id, long FromPoiId, long ToPoiId,
    decimal DistanceKm, int EstimatedDurationMinutes, TransportMode TransportMode);