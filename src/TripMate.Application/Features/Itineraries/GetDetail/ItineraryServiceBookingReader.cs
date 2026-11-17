using Microsoft.EntityFrameworkCore;

using TripMate.Application.Common.Interfaces;
using TripMate.Domain.Entities;

namespace TripMate.Application.Features.Itineraries.GetDetail;

public sealed record ItineraryBookedServiceDto(long Id, long ServiceId, long? PoiId,
    string ServiceName, string? ServiceOption, string Status, DateTimeOffset StartAtUtc,
    DateTimeOffset? EndAtUtc, int Quantity, decimal EstimatedAmount, decimal? ActualAmount);

public interface IItineraryServiceBookingReader
{
    Task<IReadOnlyList<ItineraryBookedServiceDto>> ReadAsync(Itinerary itinerary,
        CancellationToken cancellationToken);
}

public sealed class ItineraryServiceBookingReader(IApplicationDbContext db) : IItineraryServiceBookingReader
{
    public async Task<IReadOnlyList<ItineraryBookedServiceDto>> ReadAsync(Itinerary itinerary,
        CancellationToken cancellationToken)
    {
        // An itinerary version change keeps an already confirmed reservation in the same
        // owner's planning family. Contact details and special requests stay private.
        return await db.ServiceBookings.AsNoTracking()
            .Where(booking => booking.TravelerUserId == itinerary.TravelerUserId
                && booking.RequestSubmittedAtUtc != null
                && (booking.Status == ServiceBooking.Confirmed || booking.Status == ServiceBooking.Completed)
                && db.Itineraries.Any(linked => linked.Id == booking.ItineraryId
                    && linked.TravelerUserId == itinerary.TravelerUserId
                    && (linked.Id == itinerary.Id || (itinerary.SchedulingRequestId != null
                        && linked.SchedulingRequestId == itinerary.SchedulingRequestId))))
            .OrderBy(booking => booking.StartAtUtc).ThenBy(booking => booking.Id)
            .Select(booking => new ItineraryBookedServiceDto(booking.Id, booking.ServiceId,
                booking.Service.PoiId, booking.Service.Name, booking.ServiceOption, booking.Status,
                booking.StartAtUtc, booking.EndAtUtc, booking.Quantity, booking.TotalPrice, booking.ActualAmount))
            .ToListAsync(cancellationToken);
    }
}