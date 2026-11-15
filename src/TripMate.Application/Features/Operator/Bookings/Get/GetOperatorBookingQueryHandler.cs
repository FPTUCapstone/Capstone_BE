using System.Globalization;

using MediatR;

using Microsoft.EntityFrameworkCore;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Common.Models;
using TripMate.Application.Features.Bookings.Common;
using TripMate.Application.Features.Operator.Bookings.Common;
using TripMate.Application.Features.Operator.Bookings.List;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Application.Features.Operator.Bookings.Get;

public sealed class GetOperatorBookingQueryHandler(
    IApplicationDbContext dbContext,
    ICurrentUserService currentUserService)
    : IRequestHandler<GetOperatorBookingQuery, Result<OperatorBookingDetailDto>>
{
    public async Task<Result<OperatorBookingDetailDto>> Handle(
        GetOperatorBookingQuery request,
        CancellationToken cancellationToken)
    {
        Result<long> access = await OperatorBookingAccess.ResolveOperatorIdAsync(
            dbContext, currentUserService, cancellationToken);
        if (access.IsFailure)
        {
            return Result.Failure<OperatorBookingDetailDto>(access.ErrorCode!, access.ErrorMessage!);
        }

        // Resolve existence and ownership first, so the rest of the booking is read only for its owner.
        long? ownerUserId = await dbContext.Bookings.AsNoTracking()
            .Where(booking => booking.Id == request.BookingId)
            .Select(booking => (long?)booking.TourSchedule.Tour.OperatorUserId)
            .SingleOrDefaultAsync(cancellationToken);
        if (ownerUserId is null)
        {
            return Result.Failure<OperatorBookingDetailDto>(
                OperatorBookingErrorCodes.NotFound, "The booking could not be found.");
        }

        if (ownerUserId != access.Value)
        {
            return Result.Failure<OperatorBookingDetailDto>(
                OperatorBookingErrorCodes.Forbidden, "You do not have permission to access this function.");
        }

        BookingRow booking = await (
            from candidate in dbContext.Bookings.AsNoTracking()
            where candidate.Id == request.BookingId
            join booker in dbContext.Users.AsNoTracking() on candidate.TravelerUserId equals booker.Id
            select new BookingRow(
                candidate.Id,
                candidate.BookingCode,
                candidate.Status,
                candidate.PaymentStatus,
                candidate.BookedAtUtc,
                candidate.CancelledAtUtc,
                candidate.CancelReason,
                candidate.TourSchedule.Tour.Id,
                candidate.TourSchedule.Tour.Title,
                candidate.TourSchedule.Id,
                candidate.TourSchedule.StartAtUtc,
                candidate.TourSchedule.EndAtUtc,
                candidate.TourSchedule.MeetingPoint,
                booker.Id,
                booker.FullName,
                booker.Email,
                booker.PhoneNumber,
                candidate.Quantity,
                candidate.VoucherId,
                candidate.DiscountAmount,
                candidate.TotalAmount))
            .SingleAsync(cancellationToken);

        // Only the whitelisted participant columns are ever selected.
        List<OperatorBookingParticipantDto> participants = await dbContext.BookingParticipants.AsNoTracking()
            .Where(participant => participant.BookingId == booking.Id)
            .OrderBy(participant => participant.Id)
            .Select(participant => new OperatorBookingParticipantDto(participant.FullName, participant.ParticipantType))
            .ToListAsync(cancellationToken);

        List<TicketRow> tickets = await dbContext.Tickets.AsNoTracking()
            .Where(ticket => ticket.BookingId == booking.Id)
            .Select(ticket => new TicketRow(
                ticket.Id, ticket.TicketCode, ticket.Status, ticket.IssuedAtUtc, ticket.ExpiresAtUtc, ticket.CheckedInAtUtc))
            .ToListAsync(cancellationToken);
        TicketRow? latestTicket = tickets
            .OrderByDescending(ticket => ticket.IssuedAtUtc)
            .ThenByDescending(ticket => ticket.Id)
            .FirstOrDefault();
        (string checkInStatus, DateTimeOffset? checkedInAtUtc) = BookingCheckIn.Resolve(
            tickets.Select(ticket => new TicketCheckInSnapshot(ticket.Status, ticket.CheckedInAtUtc)));

        OperatorBookingPaymentDto? payment = await dbContext.PaymentTransactions.AsNoTracking()
            .Where(transaction => transaction.BookingId == booking.Id
                && transaction.TransactionType == PaymentTransaction.TypePayment
                && transaction.Status == PaymentTransactionStatus.Success)
            .OrderByDescending(transaction => transaction.CreatedAtUtc)
            .ThenByDescending(transaction => transaction.Id)
            .Select(transaction => new OperatorBookingPaymentDto(
                transaction.Gateway,
                transaction.GatewayTransactionRef,
                transaction.Status,
                transaction.Amount,
                transaction.SucceededAtUtc))
            .FirstOrDefaultAsync(cancellationToken);

        OperatorBookingCouponDto? coupon = null;
        if (booking.VoucherId is long voucherId)
        {
            coupon = await dbContext.Vouchers.AsNoTracking()
                .Where(voucher => voucher.Id == voucherId)
                .Select(voucher => new OperatorBookingCouponDto(voucher.Code, voucher.Name, booking.DiscountAmount))
                .FirstOrDefaultAsync(cancellationToken);
        }

        (bool canCancel, string? blockedReason) = BookingCancelEligibility.Evaluate(
            booking.Status, tickets.Any(ticket => ticket.Status == TicketStatus.Used));

        return Result.Success(new OperatorBookingDetailDto(
            booking.Id.ToString(CultureInfo.InvariantCulture),
            booking.BookingCode,
            booking.Status,
            booking.PaymentStatus,
            booking.BookedAtUtc,
            booking.CancelledAtUtc,
            booking.CancelReason,
            new OperatorBookingTourDto(booking.TourId.ToString(CultureInfo.InvariantCulture), booking.TourTitle),
            new OperatorBookingDetailScheduleDto(
                booking.ScheduleId.ToString(CultureInfo.InvariantCulture),
                booking.ScheduleStartAtUtc,
                booking.ScheduleEndAtUtc,
                booking.MeetingPoint),
            new OperatorBookingDetailBookerDto(
                booking.BookerId.ToString(CultureInfo.InvariantCulture),
                booking.BookerName,
                booking.BookerEmail,
                booking.BookerPhoneNumber),
            booking.Quantity,
            participants,
            latestTicket is null
                ? null
                : new OperatorBookingTicketDto(
                    latestTicket.TicketCode, latestTicket.Status, latestTicket.IssuedAtUtc, latestTicket.ExpiresAtUtc),
            checkInStatus,
            checkedInAtUtc,
            payment,
            payment?.Amount ?? 0m,
            booking.TotalAmount + booking.DiscountAmount,
            booking.DiscountAmount,
            booking.TotalAmount,
            coupon,
            canCancel,
            blockedReason));
    }

    private sealed record BookingRow(
        long Id,
        string BookingCode,
        BookingStatus Status,
        PaymentStatus PaymentStatus,
        DateTimeOffset BookedAtUtc,
        DateTimeOffset? CancelledAtUtc,
        string? CancelReason,
        long TourId,
        string TourTitle,
        long ScheduleId,
        DateTimeOffset ScheduleStartAtUtc,
        DateTimeOffset ScheduleEndAtUtc,
        string? MeetingPoint,
        long BookerId,
        string BookerName,
        string? BookerEmail,
        string? BookerPhoneNumber,
        int Quantity,
        long? VoucherId,
        decimal DiscountAmount,
        decimal TotalAmount);

    private sealed record TicketRow(
        long Id,
        string TicketCode,
        TicketStatus Status,
        DateTimeOffset IssuedAtUtc,
        DateTimeOffset? ExpiresAtUtc,
        DateTimeOffset? CheckedInAtUtc);
}