using System.Globalization;

using MediatR;

using Microsoft.EntityFrameworkCore;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Common.Models;
using TripMate.Application.Features.Admin.Bookings.Common;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Application.Features.Admin.Bookings.GetList;

public sealed class GetPlatformBookingsQueryHandler(
    IApplicationDbContext dbContext,
    ICurrentUserService currentUserService)
    : IRequestHandler<GetPlatformBookingsQuery, Result<PlatformBookingsResponseDto>>
{
    public async Task<Result<PlatformBookingsResponseDto>> Handle(
        GetPlatformBookingsQuery request,
        CancellationToken cancellationToken)
    {
        if (currentUserService.UserId is null || currentUserService.Role != nameof(UserRole.Staff))
        {
            return Result.Failure<PlatformBookingsResponseDto>(
                PlatformBookingErrorCodes.Forbidden,
                "You do not have permission to access this function.");
        }

        if (request.BookedFromUtc.HasValue && request.BookedToUtc.HasValue
            && request.BookedFromUtc.Value > request.BookedToUtc.Value)
        {
            return Result.Failure<PlatformBookingsResponseDto>(
                PlatformBookingErrorCodes.InvalidDateRange,
                PlatformBookingErrorCodes.InvalidDateRangeMessage);
        }

        IQueryable<Booking> filtered = dbContext.Bookings.AsNoTracking();

        if (!string.IsNullOrWhiteSpace(request.Keyword))
        {
            string pattern = ContainsPattern(request.Keyword.Trim());
            filtered = filtered.Where(booking =>
                EF.Functions.Like(booking.BookingCode, pattern, "\\") ||
                EF.Functions.Like(booking.TourSchedule.Tour.Title, pattern, "\\") ||
                EF.Functions.Like(booking.TourSchedule.Tour.OperatorProfile.CompanyName, pattern, "\\") ||
                dbContext.Users.Any(user => user.Id == booking.TravelerUserId &&
                    (EF.Functions.Like(user.FullName, pattern, "\\") ||
                     (user.Email != null && EF.Functions.Like(user.Email, pattern, "\\")))));
        }

        if (request.Status.HasValue)
        {
            filtered = filtered.Where(booking => booking.Status == request.Status.Value);
        }

        if (request.PaymentStatus.HasValue)
        {
            filtered = filtered.Where(booking => booking.PaymentStatus == request.PaymentStatus.Value);
        }

        if (request.BookedFromUtc.HasValue)
        {
            DateTimeOffset fromUtc = request.BookedFromUtc.Value.ToUniversalTime();
            filtered = filtered.Where(booking => booking.BookedAtUtc >= fromUtc);
        }

        if (request.BookedToUtc.HasValue)
        {
            DateTimeOffset toUtc = request.BookedToUtc.Value.ToUniversalTime();
            filtered = filtered.Where(booking => booking.BookedAtUtc <= toUtc);
        }

        if (request.TourId.HasValue)
        {
            filtered = filtered.Where(booking => booking.TourSchedule.TourId == request.TourId.Value);
        }

        int totalCount = await filtered.CountAsync(cancellationToken);
        long skipLong = ((long)request.PageNumber - 1) * request.PageSize;
        List<PageRow> rows;
        if (skipLong > int.MaxValue)
        {
            rows = [];
        }
        else
        {
            var pageQuery =
                (from booking in filtered
                 join traveler in dbContext.Users.AsNoTracking() on booking.TravelerUserId equals traveler.Id
                 orderby booking.BookedAtUtc descending, booking.Id descending
                 select new PageRow(
                     booking.Id,
                     booking.BookingCode,
                     traveler.Id,
                     traveler.FullName,
                     traveler.Email,
                     booking.TourSchedule.Tour.Id,
                     booking.TourSchedule.Tour.Title,
                     booking.TourSchedule.Tour.OperatorUserId,
                     booking.TourSchedule.Tour.OperatorProfile.CompanyName,
                     booking.TourSchedule.Id,
                     booking.TourSchedule.StartAtUtc,
                     booking.Quantity,
                     booking.TotalAmount + booking.DiscountAmount,
                     booking.DiscountAmount,
                     booking.TotalAmount,
                     booking.Status,
                     booking.PaymentStatus,
                     booking.BookedAtUtc))
                .Skip((int)skipLong)
                .Take(request.PageSize);

            rows = await pageQuery.ToListAsync(cancellationToken);
        }

        int totalPages = (int)Math.Ceiling(totalCount / (double)request.PageSize);
        return Result.Success(new PlatformBookingsResponseDto(
            request.PageNumber,
            request.PageSize,
            totalCount,
            totalPages,
            rows.Select(row => new PlatformBookingListItemDto(
                row.BookingId.ToString(CultureInfo.InvariantCulture),
                row.BookingCode,
                new PlatformBookingTravelerDto(
                    row.TravelerId.ToString(CultureInfo.InvariantCulture),
                    row.TravelerName,
                    row.TravelerEmail),
                new PlatformBookingTourDto(
                    row.TourId.ToString(CultureInfo.InvariantCulture),
                    row.TourTitle,
                    row.OperatorUserId.ToString(CultureInfo.InvariantCulture),
                    row.OperatorName,
                    row.ScheduleId.ToString(CultureInfo.InvariantCulture),
                    row.ScheduleStartAtUtc),
                row.Quantity,
                row.SubtotalAmount,
                row.DiscountAmount,
                row.TotalAmount,
                row.Status,
                row.PaymentStatus,
                row.BookedAtUtc)).ToList()));
    }

    private static string ContainsPattern(string value) =>
        $"%{value.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("%", "\\%", StringComparison.Ordinal)
            .Replace("_", "\\_", StringComparison.Ordinal)
            .Replace("[", "\\[", StringComparison.Ordinal)}%";

    private sealed record PageRow(
        long BookingId,
        string BookingCode,
        long TravelerId,
        string TravelerName,
        string? TravelerEmail,
        long TourId,
        string TourTitle,
        long OperatorUserId,
        string OperatorName,
        long ScheduleId,
        DateTimeOffset ScheduleStartAtUtc,
        int Quantity,
        decimal SubtotalAmount,
        decimal DiscountAmount,
        decimal TotalAmount,
        BookingStatus Status,
        PaymentStatus PaymentStatus,
        DateTimeOffset BookedAtUtc);
}