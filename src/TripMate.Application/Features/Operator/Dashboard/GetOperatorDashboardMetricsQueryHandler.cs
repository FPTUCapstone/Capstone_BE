using MediatR;

using Microsoft.EntityFrameworkCore;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Common.Models;
using TripMate.Application.Features.Operator.Revenue.Common;
using TripMate.Application.Features.Operator.Tours.Common;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Application.Features.Operator.Dashboard;

public sealed class GetOperatorDashboardMetricsQueryHandler(
    IApplicationDbContext db,
    ICurrentUserService currentUser,
    IDateTimeProvider clock,
    OperatorRevenueAggregator revenue)
    : IRequestHandler<GetOperatorDashboardMetricsQuery, Result<OperatorDashboardMetricsResponse>>
{
    private static readonly TimeSpan VietnamOffset = TimeSpan.FromHours(7);
    private const string RevenueGranularity = "monthly";

    public async Task<Result<OperatorDashboardMetricsResponse>> Handle(
        GetOperatorDashboardMetricsQuery request, CancellationToken cancellationToken)
    {
        if (currentUser.UserId is not long operatorId || currentUser.Role != nameof(UserRole.TourOperator))
            return Denied();

        var eligible = await db.OperatorProfiles.AsNoTracking().AnyAsync(profile =>
            profile.UserId == operatorId &&
            profile.ApprovalStatus == OperatorApprovalStatus.Approved &&
            profile.User.Role == UserRole.TourOperator &&
            profile.User.Status == AccountStatus.Active, cancellationToken);
        if (!eligible) return Denied();

        // One clock snapshot defines both the current metrics and the Vietnam calendar month.
        var now = clock.UtcNow.ToUniversalTime();
        var local = now.ToOffset(VietnamOffset);
        var monthStart = new DateOnly(local.Year, local.Month, 1);
        var monthEndExclusive = monthStart.AddMonths(1);
        var from = new DateTimeOffset(monthStart.ToDateTime(TimeOnly.MinValue), VietnamOffset).ToUniversalTime();
        var until = new DateTimeOffset(monthEndExclusive.ToDateTime(TimeOnly.MinValue), VietnamOffset).ToUniversalTime();

        var activeTours = await db.Tours.AsNoTracking().CountAsync(tour =>
            tour.OperatorUserId == operatorId &&
            (tour.Status == TourStatus.Published ||
             (tour.Status == TourStatus.Approved && tour.PublishedAtUtc <= now)) &&
            tour.Schedules.Any(schedule =>
                schedule.Status == TourScheduleStatus.Scheduled &&
                schedule.StartAtUtc > now &&
                (!schedule.RegistrationDeadlineAtUtc.HasValue || schedule.RegistrationDeadlineAtUtc > now) &&
                schedule.ReservedCapacity < schedule.TotalCapacity), cancellationToken);

        // Count orders, not transaction attempts or passengers. A later refund does not erase
        // an order while it remains Confirmed/Completed; cancelled orders are excluded.
        var bookings = await db.Bookings.AsNoTracking().CountAsync(booking =>
            booking.TourSchedule.Tour.OperatorUserId == operatorId &&
            booking.BookedAtUtc >= from && booking.BookedAtUtc < until &&
            (booking.Status == BookingStatus.Confirmed || booking.Status == BookingStatus.Completed) &&
            db.PaymentTransactions.Any(payment => payment.BookingId == booking.Id &&
                payment.TransactionType == PaymentTransaction.TypePayment &&
                payment.Status == PaymentTransactionStatus.Success), cancellationToken);

        var coupons = await db.Vouchers.AsNoTracking().CountAsync(voucher =>
            voucher.OwnerOperatorUserId == operatorId && voucher.Status == VoucherStatus.Active &&
            voucher.ValidFromUtc <= now && now < voucher.ValidToUtc &&
            (!voucher.UsageLimit.HasValue || voucher.UsedCount < voucher.UsageLimit.Value), cancellationToken);

        // Keep the report's own payment-period, settled payout, refund and commission semantics.
        var aggregate = await revenue.AggregateAsync(operatorId, monthStart,
            monthEndExclusive.AddDays(-1), RevenueGranularity, null, cancellationToken);

        return Result.Success(new OperatorDashboardMetricsResponse(activeTours, bookings,
            aggregate.Summary.NetAmount, coupons, now, monthStart, monthEndExclusive));
    }

    private static Result<OperatorDashboardMetricsResponse> Denied() =>
        Result.Failure<OperatorDashboardMetricsResponse>(OperatorTourErrorCodes.AccessRequired,
            "An active approved Tour Operator account is required.");
}