using Microsoft.EntityFrameworkCore;

using TripMate.Application.Common.Interfaces;
using TripMate.Domain.Constants;
using TripMate.Domain.Entities;

namespace TripMate.Application.Features.Scheduling.Common;

public static class PlanningPoiEligibility
{
    public static bool IsPlanningReady(PointOfInterest poi) =>
        poi.SourceUrl is not null
        && poi.VerifiedAtUtc.HasValue
        && poi.OpeningHours.Any(hours =>
            !hours.IsClosed && hours.OpenTime.HasValue && hours.CloseTime.HasValue);

    /// <summary>
    /// Restaurants and cafés are added by travelers, not auto-scheduled by generation.
    /// A POI with at least one Hotel <see cref="CommercialService"/> link is accommodation,
    /// not a sightseeing stop, regardless of that service's <c>AvailabilityStatus</c> — a
    /// hotel with every room class closed is still a hotel, never a candidate here.
    /// <paramref name="accommodationPoiIds"/> is every POI id with at least one Hotel
    /// service, loaded once by the caller via <see cref="AccommodationPoiIdsAsync"/>.
    /// </summary>
    public static bool IsAutoSchedulable(PointOfInterest poi, IReadOnlySet<long> accommodationPoiIds) =>
        !PoiCategoryNames.IsFoodAndDrink(poi.Category?.Name)
        && !accommodationPoiIds.Contains(poi.Id);

    private static readonly string[] StayCategories = ["lưu trú", "hotel", "accommodation", "resort", "homestay", "khách sạn"];

    public static IQueryable<long> AccommodationPoiIds(IApplicationDbContext db) =>
        db.PointsOfInterest.AsNoTracking()
            .Where(poi => poi.Category != null && StayCategories.Contains(poi.Category.Name.ToLower()))
            .Select(poi => poi.Id)
            .Union(db.CommercialServices.AsNoTracking()
                .Where(service => service.ServiceCategory == CommercialService.CategoryHotel && service.PoiId != null)
                .Select(service => service.PoiId!.Value));

    public static async Task<HashSet<long>> AccommodationPoiIdsAsync(
        IApplicationDbContext db, CancellationToken token) =>
        (await AccommodationPoiIds(db).ToListAsync(token)).ToHashSet();
}
