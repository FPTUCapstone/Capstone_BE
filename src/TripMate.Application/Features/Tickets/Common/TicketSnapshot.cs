using Microsoft.EntityFrameworkCore;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Common.Models;
using TripMate.Domain.Enums;

namespace TripMate.Application.Features.Tickets.Common;

/// <summary>Everything the UC-29 screen shows about one ticket, read in a single query.</summary>
public sealed record TicketSnapshot(
    long TicketId,
    string TicketCode,
    TicketStatus TicketStatus,
    DateTimeOffset? CheckedInAtUtc,
    long BookingId,
    string BookingCode,
    long TravelerUserId,
    BookingStatus BookingStatus,
    PaymentStatus PaymentStatus,
    int Quantity,
    long TourId,
    string TourTitle,
    string? OperatorName,
    DateTimeOffset StartAtUtc,
    DateTimeOffset EndAtUtc,
    string? MeetingPoint)
{
    public static Task<TicketSnapshot?> ReadAsync(IApplicationDbContext dbContext, long ticketId,
        CancellationToken cancellationToken) =>
        dbContext.Tickets.AsNoTracking()
            .Where(ticket => ticket.Id == ticketId)
            .Select(ticket => new TicketSnapshot(
                ticket.Id,
                ticket.TicketCode,
                ticket.Status,
                ticket.CheckedInAtUtc,
                ticket.BookingId,
                ticket.Booking.BookingCode,
                ticket.Booking.TravelerUserId,
                ticket.Booking.Status,
                ticket.Booking.PaymentStatus,
                ticket.Booking.Quantity,
                ticket.Booking.TourSchedule.TourId,
                ticket.Booking.TourSchedule.Tour.Title,
                dbContext.OperatorProfiles
                    .Where(profile => profile.UserId == ticket.Booking.TourSchedule.Tour.OperatorUserId)
                    .Select(profile => profile.CompanyName)
                    .FirstOrDefault(),
                ticket.Booking.TourSchedule.StartAtUtc,
                ticket.Booking.TourSchedule.EndAtUtc,
                ticket.Booking.TourSchedule.MeetingPoint))
            .SingleOrDefaultAsync(cancellationToken);

    /// <summary>BR-82: only a booking confirmed by a verified payment holds a ticket.</summary>
    public static bool IsConfirmedByPayment(BookingStatus bookingStatus, PaymentStatus paymentStatus) =>
        bookingStatus is BookingStatus.Confirmed or BookingStatus.Completed && paymentStatus == PaymentStatus.Paid;

    /// <summary>
    /// Ownership is checked first (BR-90, D6) so nothing about another Traveler's booking is revealed.
    /// A cancelled booking still shows its ticket, as Cancelled.
    /// </summary>
    public Result? CheckAccess(long travelerUserId)
    {
        if (TravelerUserId != travelerUserId)
            return Result.Failure(TicketErrorCodes.Forbidden, "This e-ticket belongs to another traveler.");

        return BookingStatus == BookingStatus.Cancelled || IsConfirmedByPayment(BookingStatus, PaymentStatus)
            ? null
            : Result.Failure(TicketErrorCodes.BookingNotConfirmed,
                "The booking has not been confirmed by a verified payment.");
    }
}
