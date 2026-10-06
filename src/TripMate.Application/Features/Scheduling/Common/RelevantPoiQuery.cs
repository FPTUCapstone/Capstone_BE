using Microsoft.EntityFrameworkCore;

using TripMate.Application.Common.Geo;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Application.Features.Scheduling.Common;

internal static class RelevantPoiQuery
{
    public static IQueryable<PointOfInterest> WithPlanningDetails(
        this IQueryable<PointOfInterest> pois,
        decimal explorationLatitude,
        decimal explorationLongitude,
        decimal searchRadiusKm,
        IEnumerable<long> mandatoryPoiIds,
        long? endPoiId)
    {
        var bounds = LocationBounds.From(
            explorationLatitude,
            explorationLongitude,
            (int)Math.Ceiling(searchRadiusKm));
        long[] mandatoryIds = mandatoryPoiIds.Distinct().ToArray();

        return pois
            .AsNoTracking()
            .AsSplitQuery()
            .Include(poi => poi.Category)
            .Include(poi => poi.OpeningHours)
            .Include(poi => poi.PoiTags)
            .ThenInclude(mapping => mapping.Tag)
            .Where(poi => poi.Status == PointOfInterestStatus.Active
                && ((poi.Latitude >= bounds.MinimumLatitude
                    && poi.Latitude <= bounds.MaximumLatitude
                    && poi.Longitude >= bounds.MinimumLongitude
                    && poi.Longitude <= bounds.MaximumLongitude)
                    || mandatoryIds.Contains(poi.Id)
                    || (endPoiId.HasValue && poi.Id == endPoiId.Value)));
    }
}