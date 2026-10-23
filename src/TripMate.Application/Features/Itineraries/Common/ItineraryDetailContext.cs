using Microsoft.EntityFrameworkCore;

using TripMate.Application.Common.Interfaces;
using TripMate.Domain.Entities;

namespace TripMate.Application.Features.Itineraries.Common;

/// <summary>The scheduling request and its end POI, which give an itinerary detail its trip-level fields.</summary>
public sealed record ItineraryDetailContext(SchedulingRequest? Request, PointOfInterest? EndPoi)
{
    public static async Task<ItineraryDetailContext> LoadAsync(
        IApplicationDbContext dbContext,
        long? schedulingRequestId,
        CancellationToken cancellationToken)
    {
        if (schedulingRequestId is null)
        {
            return new ItineraryDetailContext(null, null);
        }

        var request = await dbContext.SchedulingRequests
            .AsNoTracking()
            .SingleOrDefaultAsync(item => item.Id == schedulingRequestId.Value, cancellationToken);
        var endPoi = request?.EndPointOfInterestId is { } endPoiId
            ? await dbContext.PointsOfInterest
                .AsNoTracking()
                .SingleOrDefaultAsync(poi => poi.Id == endPoiId, cancellationToken)
            : null;
        return new ItineraryDetailContext(request, endPoi);
    }
}