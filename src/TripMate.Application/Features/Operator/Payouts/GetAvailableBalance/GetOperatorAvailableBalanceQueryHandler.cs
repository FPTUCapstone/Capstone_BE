using MediatR;

using Microsoft.EntityFrameworkCore;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Common.Models;
using TripMate.Application.Features.Operator.Payouts.Common;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Application.Features.Operator.Payouts.GetAvailableBalance;

public sealed class GetOperatorAvailableBalanceQueryHandler(
    IApplicationDbContext db,
    ICurrentUserService currentUser,
    IDateTimeProvider clock)
    : IRequestHandler<GetOperatorAvailableBalanceQuery, Result<AvailableBalanceDto>>
{
    public async Task<Result<AvailableBalanceDto>> Handle(GetOperatorAvailableBalanceQuery request, CancellationToken ct)
    {
        if (currentUser.UserId is not long operatorId || currentUser.Role != nameof(UserRole.TourOperator)
            || !await db.OperatorProfiles.AnyAsync(p => p.UserId == operatorId
                && p.ApprovalStatus == OperatorApprovalStatus.Approved && p.User.Status == AccountStatus.Active, ct))
        {
            return Result.Failure<AvailableBalanceDto>(
                OperatorPayoutErrorCodes.OperatorNotApproved, "An active approved Tour Operator account is required.");
        }

        var activePayoutCode = await db.Payouts.AsNoTracking()
            .Where(p => p.OperatorUserId == operatorId && Payout.ActiveStatuses.Contains(p.Status))
            .Select(p => p.PayoutCode)
            .FirstOrDefaultAsync(ct);

        var reader = new OperatorPayoutEligibilityReader(db);
        var nowUtc = clock.UtcNow;
        var refundHoldDays = await reader.LoadIntConfigAsync(
            PayoutConfigKeys.RefundHoldDays, PayoutConfigKeys.DefaultRefundHoldDays, ct);
        var minimumAmount = await reader.LoadDecimalConfigAsync(
            PayoutConfigKeys.MinimumAmountVnd, PayoutConfigKeys.DefaultMinimumAmountVnd, ct);

        var eligibleBookings = await reader.LoadEligibleBookingsAsync(operatorId, nowUtc, refundHoldDays, ct);
        var closedMonths = eligibleBookings
            .Select(b => (Booking: b, Month: OperatorPayoutEligibilityReader.SettlementMonthOf(b.PayoutEligibleAtUtc)))
            .Where(x => OperatorPayoutEligibilityReader.IsMonthClosed(x.Month, nowUtc))
            .ToList();

        var bookingIds = closedMonths.Select(x => x.Booking.BookingId).ToList();
        var refunds = await reader.LoadProcessedRefundsAsync(bookingIds, ct);
        var payments = closedMonths.Select(x => new PayoutPaymentInput(
            x.Booking.BookingId, x.Booking.PaymentTransactionId, x.Booking.PaidAmount, x.Booking.DiscountAmount,
            x.Booking.CommissionRateApplied, x.Booking.ParticipantCount)).ToList();

        var outstandingAdjustments = await db.PayoutAdjustments.AsNoTracking()
            .Where(a => a.OperatorUserId == operatorId && a.AppliedPayoutId == null)
            .Select(a => a.Amount)
            .ToListAsync(ct);
        var adjustmentsOutstandingTotal = -outstandingAdjustments.Sum();

        var periodCount = closedMonths.Select(x => x.Month).Distinct().Count();
        var calculation = OperatorPayoutCalculator.Calculate(payments, refunds, []);
        // Display approximation only: caps at zero so the card never shows a negative balance.
        // The authoritative, ledger-row-accurate figure is recomputed by RequestPayoutCommandHandler.
        var payableAfterAdjustments = Math.Max(0m, calculation.PayableNetBeforeAdjustments - adjustmentsOutstandingTotal);

        string? blockReason = null;
        if (activePayoutCode is not null) blockReason = "ACTIVE_PAYOUT_EXISTS";
        else if (payments.Count == 0) blockReason = "NO_ELIGIBLE_BOOKING";
        else if (calculation.PayableNetBeforeAdjustments < 0) blockReason = "PAYOUT_NOT_REQUESTABLE";
        else if (payableAfterAdjustments < minimumAmount) blockReason = "BELOW_MINIMUM_PAYOUT";

        return Result.Success(new AvailableBalanceDto(
            periodCount, calculation.PaymentsReceived, calculation.DiscountsGiven, calculation.RefundedAmount,
            calculation.NetSales, calculation.CommissionAmount, adjustmentsOutstandingTotal,
            payableAfterAdjustments, blockReason is null, blockReason, activePayoutCode));
    }
}
