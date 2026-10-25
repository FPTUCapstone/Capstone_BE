using MediatR;

using TripMate.Application.Common.Models;
using TripMate.Application.Features.Tickets.Common;

namespace TripMate.Application.Features.Tickets.Get;

/// <summary>
/// UC-29 view, keyed by the ticket (Trip History link) or by the booking (Booking Confirmation screen).
/// Exactly one of <see cref="TicketId"/> and <see cref="BookingId"/> is set.
/// </summary>
public sealed record GetTravelerTicketQuery(long TravelerUserId, long? TicketId, long? BookingId)
    : IRequest<Result<TicketResponse>>
{
    public static GetTravelerTicketQuery ForTicket(long travelerUserId, long ticketId) =>
        new(travelerUserId, ticketId, null);

    public static GetTravelerTicketQuery ForBooking(long travelerUserId, long bookingId) =>
        new(travelerUserId, null, bookingId);
}
