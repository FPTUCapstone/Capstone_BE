using Microsoft.EntityFrameworkCore;

using TripMate.Application.Common.Geo;
using TripMate.Application.Common.Interfaces;
using TripMate.Application.Features.Scheduling.Common;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Application.Features.Rerouting.Engine;

internal sealed record ReroutingCandidateQuery(
    PointOfInterest Affected,
    decimal RadiusKm,
    ISet<long> ExcludedPoiIds,
    IReadOnlySet<string> PreferenceTokens,
    int Limit,
    bool RequireReviewedIndoor = false,
    bool RequireSameCategory = false);

/// <summary>
/// Replacement POIs for an affected stop (spec 4.3): Active and planning-ready, not already part of the trip, within
/// the rerouting radius of the affected stop. Ordered by same category first, then distance to the affected stop,
/// then how well the POI matches the Traveler's saved interests. Opening hours are checked later by the evaluator at
/// the real arrival time.
/// </summary>
internal sealed class ReroutingCandidateFinder(IApplicationDbContext dbContext)
{
    public async Task<IReadOnlyList<PointOfInterest>> FindAsync(
        ReroutingCandidateQuery query,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        var affected = query.Affected;
        var pois = await dbContext.PointsOfInterest
            .WithPlanningDetails(affected.Latitude, affected.Longitude, query.RadiusKm, [], endPoiId: null)
            .ToListAsync(cancellationToken);
        var accommodationPoiIds = await PlanningPoiEligibility.AccommodationPoiIdsAsync(dbContext, cancellationToken);

        return pois
            .Where(poi => poi.Id != affected.Id && !query.ExcludedPoiIds.Contains(poi.Id))
            .Where(poi => !accommodationPoiIds.Contains(poi.Id))
            .Where(PlanningPoiEligibility.IsPlanningReady)
            .Where(poi => !query.RequireReviewedIndoor
                || (poi.IndoorOutdoor == IndoorOutdoorType.Indoor && poi.IndoorOutdoorReviewedAtUtc.HasValue))
            .Where(poi => !query.RequireSameCategory || poi.CategoryId == affected.CategoryId)
            .Select(poi => new
            {
                Poi = poi,
                Distance = GeoDistance.EquirectangularKilometers(
                    affected.Latitude, affected.Longitude, poi.Latitude, poi.Longitude),
            })
            .Where(entry => entry.Distance <= query.RadiusKm)
            .OrderByDescending(entry => entry.Poi.CategoryId == affected.CategoryId)
            .ThenBy(entry => entry.Distance)
            .ThenByDescending(entry => TravelerPreferenceScoring.CalculatePreferenceScore(entry.Poi, query.PreferenceTokens))
            .ThenBy(entry => entry.Poi.Id)
            .Take(query.Limit)
            .Select(entry => entry.Poi)
            .ToArray();
    }
}