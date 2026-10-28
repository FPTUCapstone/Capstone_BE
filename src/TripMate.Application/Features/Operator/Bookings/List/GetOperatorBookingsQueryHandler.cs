using System.Globalization;

using MediatR;

using Microsoft.EntityFrameworkCore;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Common.Models;
using TripMate.Application.Features.Admin.ActiveTrips.GetList;
using TripMate.Application.Features.Operator.Bookings.Common;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Application.Features.Operator.Bookings.List;

public sealed class GetOperatorBookingsQueryHandler(
    IApplicationDbContext dbContext,
    ICurrentUserService currentUserService)
    : IRequestHandler<GetOperatorBookingsQuery, Result<OperatorBookingsResponseDto>>
{
    public async Task<Result<OperatorBookingsResponseDto>> Handle(
        GetOperatorBookingsQuery request,
        CancellationToken cancellationToken)
    {
        Result<long> access = await OperatorBookingAccess.ResolveOperatorIdAsync(
            dbContext, currentUserService, cancellationToken);
        if (access.IsFailure)
        {
            return Result.Failure<OperatorBookingsResponseDto>(access.ErrorCode!, access.ErrorMessage!);
        }

        long operatorId = access.Value;

        if (request.DepartureFrom.HasValue && request.DepartureTo.HasValue
            && request.DepartureFrom.Value > request.DepartureTo.Value)
        {
            return Result.Failure<OperatorBookingsResponseDto>(
                OperatorBookingErrorCodes.InvalidDateRange,
                "The departure date range is logically invalid.");
        }

        // An unowned and a nonexistent id are indistinguishable: both are forbidden (BR-105).
        if (request.TourId is long tourId && !await dbContext.Tours.AsNoTracking().AnyAsync(
                tour => tour.Id == tourId && tour.OperatorUserId == operatorId, cancellationToken))
        {
            return Forbidden();
        }

        if (request.ScheduleId is long scheduleId && !await dbContext.TourSchedules.AsNoTracking().AnyAsync(
                schedule => schedule.Id == scheduleId && schedule.Tour.OperatorUserId == operatorId,
                cancellationToken))
        {
            return Forbidden();
        }

        // BR-105: the ownership predicate comes first and every later filter narrows within it.
        IQueryable<Booking> filtered = dbContext.Bookings.AsNoTracking()
            .Where(booking => booking.TourSchedule.Tour.OperatorUserId == operatorId);

        if (request.TourId.HasValue)
        {
            filtered = filtered.Where(booking => booking.TourSchedule.TourId == request.TourId.Value);
        }

        if (request.ScheduleId.HasValue)
        {
            filtered = filtered.Where(booking => booking.TourScheduleId == request.ScheduleId.Value);
        }

        if (request.Status.HasValue)
        {
            filtered = filtered.Where(booking => booking.Status == request.Status.Value);
        }

        if (request.PaymentStatus.HasValue)
        {
            filtered = filtered.Where(booking => booking.PaymentStatus == request.PaymentStatus.Value);
        }

        if (!string.IsNullOrWhiteSpace(request.Keyword))
        {
            string pattern = ContainsPattern(request.Keyword.Trim());
            filtered = filtered.Where(booking =>
                EF.Functions.Like(booking.BookingCode, pattern, "\\") ||
                dbContext.Users.Any(booker => booker.Id == booking.TravelerUserId &&
                    (EF.Functions.Like(booker.FullName, pattern, "\\") ||
                     (booker.Email != null && EF.Functions.Like(booker.Email, pattern, "\\")))));
        }

        if (request.DepartureFrom.HasValue && request.DepartureFrom.Value > DateOnly.MinValue.AddDays(1))
        {
            DateTimeOffset fromUtc = VietnamCalendar.StartUtc(request.DepartureFrom.Value);
            filtered = filtered.Where(booking => booking.TourSchedule.StartAtUtc >= fromUtc);
        }

        if (request.DepartureTo.HasValue && request.DepartureTo.Value < DateOnly.MaxValue.AddDays(-1))
        {
            // Inclusive through the end of the local day: strictly before the next local midnight.
            DateTimeOffset beforeUtc = VietnamCalendar.StartUtc(request.DepartureTo.Value.AddDays(1));
            filtered = filtered.Where(booking => booking.TourSchedule.StartAtUtc < beforeUtc);
        }

        int totalCount = await filtered.CountAsync(cancellationToken);
        OperatorBookingSummaryDto summary = await BuildSummaryAsync(filtered, totalCount, cancellationToken);

        long skip = ((long)request.PageNumber - 1) * request.PageSize;
        List<PageRow> rows = [];
        if (skip <= int.MaxValue)
        {
            rows = await (
                from booking in filtered
                join booker in dbContext.Users.AsNoTracking() on booking.TravelerUserId equals booker.Id
                orderby booking.BookedAtUtc descending, booking.Id descending
                select new PageRow(
                    booking.Id,
                    booking.BookingCode,
                    booking.TourSchedule.Tour.Id,
                    booking.TourSchedule.Tour.Title,
                    booking.TourSchedule.Id,
                    booking.TourSchedule.StartAtUtc,
                    booking.TourSchedule.EndAtUtc,
                    booker.Id,
                    booker.FullName,
                    booker.Email,
                    booking.Quantity,
                    booking.TotalAmount,
                    booking.DiscountAmount,
                    booking.Status,
                    booking.PaymentStatus,
                    booking.BookedAtUtc))
                .Skip((int)skip)
                .Take(request.PageSize)
                .ToListAsync(cancellationToken);
        }

        // Check-in is derived in memory from a light ticket snapshot of this page only (at most
        // MaximumPageSize bookings), which keeps the SQL to a plain key lookup.
        Dictionary<long, List<TicketCheckInSnapshot>> ticketsByBooking = await LoadTicketSnapshotsAsync(
            rows.Select(row => row.BookingId).ToList(), cancellationToken);

        int totalPages = (int)Math.Ceiling(totalCount / (double)request.PageSize);
        return Result.Success(new OperatorBookingsResponseDto(
            request.PageNumber,
            request.PageSize,
            totalCount,
            totalPages,
            summary,
            rows.Select(row => ToItem(
                row,
                BookingCheckIn.Resolve(ticketsByBooking.GetValueOrDefault(row.BookingId) ?? []))).ToList()));
    }

    private async Task<Dictionary<long, List<TicketCheckInSnapshot>>> LoadTicketSnapshotsAsync(
        List<long> bookingIds, CancellationToken cancellationToken)
    {
        if (bookingIds.Count == 0)
        {
            return [];
        }

        var tickets = await dbContext.Tickets.AsNoTracking()
            .Where(ticket => bookingIds.Contains(ticket.BookingId))
            .Select(ticket => new { ticket.BookingId, ticket.Status, ticket.CheckedInAtUtc })
            .ToListAsync(cancellationToken);

        return tickets
            .GroupBy(ticket => ticket.BookingId)
            .ToDictionary(
                group => group.Key,
                group => group.Select(ticket => new TicketCheckInSnapshot(ticket.Status, ticket.CheckedInAtUtc)).ToList());
    }

    private static string ContainsPattern(string value) =>
        $"%{value.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("%", "\\%", StringComparison.Ordinal)
            .Replace("_", "\\_", StringComparison.Ordinal)
            .Replace("[", "\\[", StringComparison.Ordinal)}%";

    private static Result<OperatorBookingsResponseDto> Forbidden() =>
        Result.Failure<OperatorBookingsResponseDto>(
            OperatorBookingErrorCodes.Forbidden,
            "You do not have permission to access this function.");

    private static async Task<OperatorBookingSummaryDto> BuildSummaryAsync(
        IQueryable<Booking> filtered, int totalCount, CancellationToken cancellationToken)
    {
        // Participants and amount share one basis: only bookings that will actually travel.
        IQueryable<Booking> confirmed = filtered.Where(booking =>
            booking.Status == BookingStatus.Confirmed || booking.Status == BookingStatus.Completed);
        int participants = await confirmed.SumAsync(booking => (int?)booking.Quantity, cancellationToken) ?? 0;
        decimal amount = await confirmed.SumAsync(booking => (decimal?)booking.TotalAmount, cancellationToken) ?? 0m;
        return new OperatorBookingSummaryDto(totalCount, participants, amount);
    }

    private static OperatorBookingListItemDto ToItem(
        PageRow row, (string Status, DateTimeOffset? CheckedInAtUtc) checkIn) => new(
        row.BookingId.ToString(CultureInfo.InvariantCulture),
        row.BookingCode,
        new OperatorBookingTourDto(row.TourId.ToString(CultureInfo.InvariantCulture), row.TourTitle),
        new OperatorBookingScheduleDto(
            row.ScheduleId.ToString(CultureInfo.InvariantCulture), row.ScheduleStartAtUtc, row.ScheduleEndAtUtc),
        new OperatorBookingBookerDto(
            row.BookerId.ToString(CultureInfo.InvariantCulture), row.BookerName, row.BookerEmail),
        row.Quantity,
        row.TotalAmount,
        row.DiscountAmount,
        row.Status,
        row.PaymentStatus,
        checkIn.Status,
        checkIn.CheckedInAtUtc,
        row.BookedAtUtc);

    private sealed record PageRow(
        long BookingId,
        string BookingCode,
        long TourId,
        string TourTitle,
        long ScheduleId,
        DateTimeOffset ScheduleStartAtUtc,
        DateTimeOffset ScheduleEndAtUtc,
        long BookerId,
        string BookerName,
        string? BookerEmail,
        int Quantity,
        decimal TotalAmount,
        decimal DiscountAmount,
        BookingStatus Status,
        PaymentStatus PaymentStatus,
        DateTimeOffset BookedAtUtc);
}