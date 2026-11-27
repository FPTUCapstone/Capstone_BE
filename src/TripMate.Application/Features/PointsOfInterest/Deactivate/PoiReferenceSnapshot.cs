using System.Security.Cryptography;
using System.Text.Json;

using Microsoft.EntityFrameworkCore;

using TripMate.Application.Common.Interfaces;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Application.Features.PointsOfInterest.Deactivate;

internal sealed record PoiReferenceSnapshot(
    IReadOnlyList<long> ItineraryIds,
    IReadOnlyList<long> RouteIds,
    IReadOnlyList<long> BookingIds)
{
    public static IQueryable<RouteSegment> ActiveRoutes(IApplicationDbContext db, long poiId) =>
        db.RouteSegments.Where(route => (route.FromPoiId == poiId || route.ToPoiId == poiId)
            && route.Status == RouteStatus.Active);

    public static async Task<PoiReferenceSnapshot> ReadAsync(
        IApplicationDbContext db,
        long poiId,
        IReadOnlyList<long> orderedRouteIds,
        CancellationToken cancellationToken)
    {
        var itineraryIds = await db.ItineraryItems
            .Where(item => item.PointOfInterestId == poiId && item.Itinerary.Status == Itinerary.ActiveStatus)
            .Select(item => item.ItineraryId)
            .Distinct()
            .OrderBy(id => id)
            .ToListAsync(cancellationToken);

        var bookingIds = await db.ServiceBookings
            .Where(booking => (booking.Status == ServiceBooking.PendingConfirmation || booking.Status == ServiceBooking.Confirmed)
                && db.CommercialServices.Any(service => service.Id == booking.ServiceId && service.PoiId == poiId))
            .Select(booking => booking.Id)
            .OrderBy(id => id)
            .ToListAsync(cancellationToken);

        return new PoiReferenceSnapshot(itineraryIds, orderedRouteIds, bookingIds);
    }

    public string CreateToken(long poiId) => Convert.ToHexString(SHA256.HashData(
        JsonSerializer.SerializeToUtf8Bytes(new { poiId, ItineraryIds, RouteIds, BookingIds })));
}