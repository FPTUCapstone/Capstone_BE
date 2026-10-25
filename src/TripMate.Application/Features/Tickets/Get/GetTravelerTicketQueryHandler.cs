using MediatR;

using Microsoft.EntityFrameworkCore;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Common.Models;
using TripMate.Application.Features.Tickets.Common;
using TripMate.Domain.Enums;

namespace TripMate.Application.Features.Tickets.Get;

/// <summary>
/// UC-29 View QR E-ticket (spec §5.3, §5.4). Viewing never changes a ticket or booking status; it may
/// create the ticket's first Active QR. A paid booking without a ticket is reported, never fixed here.
/// </summary>
public sealed class GetTravelerTicketQueryHandler(
    IApplicationDbContext dbContext,
    ITicketQrCodec codec,
    ITicketQrPersistenceErrorClassifier classifier,
    IDateTimeProvider clock)
    : IRequestHandler<GetTravelerTicketQuery, Result<TicketResponse>>
{
    public async Task<Result<TicketResponse>> Handle(GetTravelerTicketQuery request,
        CancellationToken cancellationToken)
    {
        long ticketId;
        if (request.BookingId is { } bookingId)
        {
            var resolved = await ResolveTicketOfBookingAsync(request.TravelerUserId, bookingId, cancellationToken);
            if (resolved.IsFailure)
                return Result.Failure<TicketResponse>(resolved.ErrorCode!, resolved.ErrorMessage!);
            ticketId = resolved.Value;
        }
        else
        {
            ticketId = request.TicketId ?? throw new ArgumentException("A ticket or booking id is required.",
                nameof(request));
        }

        var snapshot = await TicketSnapshot.ReadAsync(dbContext, ticketId, cancellationToken);
        if (snapshot is null)
            return NotFound();

        if (snapshot.CheckAccess(request.TravelerUserId) is { } denied)
            return Result.Failure<TicketResponse>(denied.ErrorCode!, denied.ErrorMessage!);

        var response = await TicketResponseBuilder.BuildAsync(dbContext,
            new TicketQrIssuer(dbContext, codec, classifier), snapshot, clock.UtcNow, cancellationToken);
        return Result.Success(response);
    }

    private async Task<Result<long>> ResolveTicketOfBookingAsync(long travelerUserId, long bookingId,
        CancellationToken cancellationToken)
    {
        var booking = await dbContext.Bookings.AsNoTracking()
            .Where(candidate => candidate.Id == bookingId)
            .Select(candidate => new
            {
                candidate.TravelerUserId,
                candidate.Status,
                candidate.PaymentStatus,
                TicketId = candidate.Tickets.OrderBy(ticket => ticket.Id).Select(ticket => (long?)ticket.Id)
                    .FirstOrDefault(),
            })
            .SingleOrDefaultAsync(cancellationToken);

        if (booking is null)
            return Result.Failure<long>(TicketErrorCodes.NotFound, "The booking was not found.");
        if (booking.TravelerUserId != travelerUserId)
            return Result.Failure<long>(TicketErrorCodes.Forbidden, "This booking belongs to another traveler.");
        if (booking.TicketId is { } ticketId)
            return Result.Success(ticketId);

        return TicketSnapshot.IsConfirmedByPayment(booking.Status, booking.PaymentStatus)
            ? Result.Failure<long>(TicketErrorCodes.NotIssued, "The e-ticket of this booking has not been issued yet.")
            : Result.Failure<long>(TicketErrorCodes.BookingNotConfirmed,
                "The booking has not been confirmed by a verified payment.");
    }

    private static Result<TicketResponse> NotFound() =>
        Result.Failure<TicketResponse>(TicketErrorCodes.NotFound, "The e-ticket was not found.");
}