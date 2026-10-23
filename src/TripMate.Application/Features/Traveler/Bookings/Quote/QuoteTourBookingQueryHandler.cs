using MediatR;

using Microsoft.EntityFrameworkCore;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Common.Models;
using TripMate.Application.Features.Traveler.Bookings.Common;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Application.Features.Traveler.Bookings.Quote;

public sealed class QuoteTourBookingQueryHandler(
    IApplicationDbContext dbContext, ICurrentUserService currentUser, IDateTimeProvider clock)
    : IRequestHandler<QuoteTourBookingQuery, Result<QuoteTourBookingResponse>>
{
    public async Task<Result<QuoteTourBookingResponse>> Handle(
        QuoteTourBookingQuery request, CancellationToken cancellationToken)
    {
        if (currentUser.UserId is not long travelerId || currentUser.Role != nameof(UserRole.Traveler))
            return AccessDenied();

        var isActiveTraveler = await dbContext.Users.AsNoTracking().AnyAsync(user =>
            user.Id == travelerId && user.Role == UserRole.Traveler && user.Status == AccountStatus.Active,
            cancellationToken);
        if (!isActiveTraveler) return AccessDenied();

        if (!long.TryParse(request.TourScheduleId, out var scheduleId) || scheduleId <= 0)
            return Result.Failure<QuoteTourBookingResponse>(
                TourBookingErrorCodes.ScheduleNotFound, "The selected tour departure was not found.");

        var now = clock.UtcNow;
        var schedule = await dbContext.TourSchedules.AsNoTracking()
            .Include(scheduleEntity => scheduleEntity.Tour)
            .FirstOrDefaultAsync(scheduleEntity => scheduleEntity.Id == scheduleId, cancellationToken);
        if (schedule is null || !IsPubliclyBookableTour(schedule.Tour, now))
            return Result.Failure<QuoteTourBookingResponse>(
                TourBookingErrorCodes.ScheduleNotFound, "The selected tour departure was not found.");

        if (!schedule.IsOpenForBooking(now))
            return Result.Failure<QuoteTourBookingResponse>(
                TourBookingErrorCodes.ScheduleNotOpen, "This tour departure is no longer open for booking.");

        var participantTypes = ExpandParticipantTypes(request.ParticipantCounts);
        if (participantTypes.Count == 0)
            return Result.Failure<QuoteTourBookingResponse>(
                TourBookingErrorCodes.ParticipantsRequired, "At least one participant is required.");

        if (participantTypes.Count > schedule.RemainingCapacity)
            return Result.Failure<QuoteTourBookingResponse>(
                TourBookingErrorCodes.InsufficientCapacity,
                $"Only {schedule.RemainingCapacity} slot(s) remain for this departure.");

        Voucher? voucher = null;
        if (!string.IsNullOrWhiteSpace(request.VoucherCode))
        {
            var normalizedCode = request.VoucherCode.Trim().ToUpperInvariant();
            voucher = await dbContext.Vouchers.AsNoTracking()
                .Include(v => v.ApplicableTours)
                .FirstOrDefaultAsync(v => v.Code == normalizedCode, cancellationToken);
            if (voucher is null)
                return Result.Failure<QuoteTourBookingResponse>(
                    TourBookingErrorCodes.VoucherNotFound, "The voucher code was not found.");
        }

        var redemptionCount = voucher is null
            ? 0
            : await dbContext.VoucherRedemptions.AsNoTracking()
                .Include(redemption => redemption.Booking)
                .CountAsync(redemption => redemption.VoucherId == voucher.Id &&
                    redemption.Booking.TravelerUserId == travelerId, cancellationToken);

        var pricing = TourBookingPricingCalculator.Calculate(
            schedule, schedule.Tour.BasePrice, participantTypes, voucher, schedule.TourId, redemptionCount, now);
        if (pricing.IsFailure)
            return Result.Failure<QuoteTourBookingResponse>(pricing.ErrorCode!, pricing.ErrorMessage!);

        var breakdown = pricing.Value.Lines
            .Select(line => new QuotePriceBreakdownLine(line.ParticipantType, line.Quantity, line.UnitPrice, line.LineAmount))
            .ToList();

        return Result.Success(new QuoteTourBookingResponse(
            schedule.TourId.ToString(),
            schedule.Id.ToString(),
            "VND",
            request.ParticipantCounts,
            breakdown,
            pricing.Value.SubtotalAmount,
            pricing.Value.DiscountAmount,
            pricing.Value.TotalAmount,
            schedule.RemainingCapacity,
            pricing.Value.AppliedVoucherCode is null ? null : new QuoteAppliedVoucher(pricing.Value.AppliedVoucherCode)));
    }

    private static bool IsPubliclyBookableTour(Tour tour, DateTimeOffset now) =>
        tour.Status == TourStatus.Published ||
        (tour.Status == TourStatus.Approved && tour.PublishedAtUtc.HasValue && tour.PublishedAtUtc <= now);

    private static List<ParticipantType> ExpandParticipantTypes(QuoteParticipantCounts counts)
    {
        var types = new List<ParticipantType>(counts.Total);
        types.AddRange(Enumerable.Repeat(ParticipantType.Adult, Math.Max(counts.Adult, 0)));
        types.AddRange(Enumerable.Repeat(ParticipantType.Child, Math.Max(counts.Child, 0)));
        types.AddRange(Enumerable.Repeat(ParticipantType.Infant, Math.Max(counts.Infant, 0)));
        return types;
    }

    private static Result<QuoteTourBookingResponse> AccessDenied() =>
        Result.Failure<QuoteTourBookingResponse>(
            TourBookingErrorCodes.AccessRequired, "An active Traveler account is required.");
}