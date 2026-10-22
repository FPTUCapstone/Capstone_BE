using MediatR;

using Microsoft.EntityFrameworkCore;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Common.Models;
using TripMate.Application.Features.Operator.Payouts.Common;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Application.Features.Operator.Payouts.GetSettlementMonths;

public sealed class GetOperatorSettlementMonthsQueryHandler(
    IApplicationDbContext db,
    ICurrentUserService currentUser,
    IDateTimeProvider clock)
    : IRequestHandler<GetOperatorSettlementMonthsQuery, Result<IReadOnlyList<SettlementMonthDto>>>
{
    public async Task<Result<IReadOnlyList<SettlementMonthDto>>> Handle(
        GetOperatorSettlementMonthsQuery request, CancellationToken ct)
    {
        if (currentUser.UserId is not long operatorId || currentUser.Role != nameof(UserRole.TourOperator)
            || !await db.OperatorProfiles.AnyAsync(p => p.UserId == operatorId
                && p.ApprovalStatus == OperatorApprovalStatus.Approved && p.User.Status == AccountStatus.Active, ct))
        {
            return Result.Failure<IReadOnlyList<SettlementMonthDto>>(
                OperatorPayoutErrorCodes.OperatorNotApproved, "An active approved Tour Operator account is required.");
        }

        var reader = new OperatorPayoutEligibilityReader(db);
        var nowUtc = clock.UtcNow;
        var refundHoldDays = await reader.LoadIntConfigAsync(
            PayoutConfigKeys.RefundHoldDays, PayoutConfigKeys.DefaultRefundHoldDays, ct);

        var year = request.Year ?? TimeZoneInfo.ConvertTime(nowUtc, OperatorPayoutEligibilityReader.VietnamTimeZone).Year;
        var results = new Dictionary<DateOnly, SettlementMonthDto>();

        // Months already claimed by a live payout (not Rejected/Cancelled) show that payout's own
        // frozen breakdown figures, never a fresh recompute — those bookings are "consumed".
        var claimedBreakdowns = await db.PayoutPeriodBreakdowns.AsNoTracking()
            .Where(b => b.SettlementMonth.Year == year
                && db.Payouts.Any(p => p.Id == b.PayoutId && p.OperatorUserId == operatorId
                    && p.Status != Payout.StatusRejected && p.Status != Payout.StatusCancelled))
            .Select(b => new
            {
                b.SettlementMonth, b.EligibleBookingCount, b.PaymentsReceived, b.DiscountsGiven,
                b.RefundedAmount, b.CommissionAmount, b.PayableNetAmount,
                PayoutCode = db.Payouts.Where(p => p.Id == b.PayoutId).Select(p => p.PayoutCode).FirstOrDefault(),
            })
            .ToListAsync(ct);

        foreach (var b in claimedBreakdowns)
        {
            var monthClosed = OperatorPayoutEligibilityReader.IsMonthClosed(b.SettlementMonth, nowUtc);
            results[b.SettlementMonth] = new SettlementMonthDto(
                b.SettlementMonth.Year, b.SettlementMonth.Month, monthClosed ? "Closed" : "Open",
                b.EligibleBookingCount, b.PaymentsReceived, b.DiscountsGiven, b.RefundedAmount,
                b.PaymentsReceived - b.RefundedAmount, b.CommissionAmount, b.PayableNetAmount,
                "IncludedInPayout", b.PayoutCode);
        }

        // Remaining eligible-but-unclaimed bookings, grouped by month, for months not already
        // covered above.
        var eligibleBookings = await reader.LoadEligibleBookingsAsync(operatorId, nowUtc, refundHoldDays, ct);
        var byMonth = eligibleBookings
            .Select(b => (Booking: b, Month: OperatorPayoutEligibilityReader.SettlementMonthOf(b.PayoutEligibleAtUtc)))
            .Where(x => x.Month.Year == year && !results.ContainsKey(x.Month))
            .GroupBy(x => x.Month);

        foreach (var group in byMonth)
        {
            var bookings = group.Select(x => x.Booking).ToList();
            var bookingIds = bookings.Select(x => x.BookingId).ToList();
            var refunds = await reader.LoadProcessedRefundsAsync(bookingIds, ct);
            var payments = bookings.Select(x => new PayoutPaymentInput(
                x.BookingId, x.PaymentTransactionId, x.PaidAmount, x.DiscountAmount,
                x.CommissionRateApplied, x.ParticipantCount)).ToList();
            var calculation = OperatorPayoutCalculator.Calculate(payments, refunds, []);
            var monthClosed = OperatorPayoutEligibilityReader.IsMonthClosed(group.Key, nowUtc);

            results[group.Key] = new SettlementMonthDto(
                group.Key.Year, group.Key.Month, monthClosed ? "Closed" : "Open", bookings.Count,
                calculation.PaymentsReceived, calculation.DiscountsGiven, calculation.RefundedAmount,
                calculation.NetSales, calculation.CommissionAmount, calculation.PayableNetBeforeAdjustments,
                monthClosed ? "AvailableBalance" : "Open", null);
        }

        return Result.Success<IReadOnlyList<SettlementMonthDto>>(
            results.Values.OrderBy(m => m.Year).ThenBy(m => m.Month).ToList());
    }
}
