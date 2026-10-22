using MediatR;

using Microsoft.EntityFrameworkCore;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Common.Models;
using TripMate.Application.Features.Operator.Payouts.Common;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Application.Features.Operator.Payouts.RequestPayout;

/// <summary>
/// "Available balance" request handler (UC-46, decided 2026-10-23). Aggregates every closed,
/// eligible, not-yet-paid settlement month into a single payout, so an operator whose per-month
/// payable never individually reaches the minimum is not stuck forever — the balance carries
/// forward and accumulates until one combined request clears it.
/// </summary>
public sealed class RequestPayoutCommandHandler(
    IApplicationDbContext db,
    ICurrentUserService currentUser,
    IDateTimeProvider clock,
    IPayoutPersistenceErrorClassifier persistenceErrors)
    : IRequestHandler<RequestPayoutCommand, Result<PayoutSummaryDto>>
{
    public async Task<Result<PayoutSummaryDto>> Handle(RequestPayoutCommand request, CancellationToken ct)
    {
        if (currentUser.UserId is not long operatorId || currentUser.Role != nameof(UserRole.TourOperator))
        {
            return Result.Failure<PayoutSummaryDto>(
                OperatorPayoutErrorCodes.OperatorNotApproved, "An active approved Tour Operator account is required.");
        }

        var operatorProfile = await db.OperatorProfiles
            .FirstOrDefaultAsync(p => p.UserId == operatorId, ct);
        var isActiveUser = await db.Users.AnyAsync(
            user => user.Id == operatorId && user.Status == AccountStatus.Active,
            ct);
        if (operatorProfile is null)
        {
            return Result.Failure<PayoutSummaryDto>(
                OperatorPayoutErrorCodes.OperatorNotApproved, "An active approved Tour Operator account is required.");
        }

        if (operatorProfile.ApprovalStatus != OperatorApprovalStatus.Approved || !isActiveUser)
        {
            return Result.Failure<PayoutSummaryDto>(
                OperatorPayoutErrorCodes.OperatorNotApproved, "An active approved Tour Operator account is required.");
        }

        if (!operatorProfile.HasBeneficiaryBankAccount)
        {
            return Result.Failure<PayoutSummaryDto>(
                OperatorPayoutErrorCodes.ValidationRequiredField, "A beneficiary bank account must be set before requesting a payout.");
        }

        // BR-113 pre-check (defense in depth; the final guard is the per-operator active-payout
        // unique index, which the InMemory test provider does not enforce).
        var hasActivePayout = await db.Payouts.AnyAsync(
            p => p.OperatorUserId == operatorId && Payout.ActiveStatuses.Contains(p.Status), ct);
        if (hasActivePayout)
        {
            return Result.Failure<PayoutSummaryDto>(
                OperatorPayoutErrorCodes.ActivePayoutExists, "A payout request is already in progress for this operator.");
        }

        var reader = new OperatorPayoutEligibilityReader(db);
        var nowUtc = clock.UtcNow;
        var refundHoldDays = await reader.LoadIntConfigAsync(
            PayoutConfigKeys.RefundHoldDays, PayoutConfigKeys.DefaultRefundHoldDays, ct);
        var minimumAmount = await reader.LoadDecimalConfigAsync(
            PayoutConfigKeys.MinimumAmountVnd, PayoutConfigKeys.DefaultMinimumAmountVnd, ct);

        var eligibleBookings = await reader.LoadEligibleBookingsAsync(operatorId, nowUtc, refundHoldDays, ct);
        var closedMonthGroups = eligibleBookings
            .Select(b => (Booking: b, Month: OperatorPayoutEligibilityReader.SettlementMonthOf(b.PayoutEligibleAtUtc)))
            .GroupBy(x => x.Month)
            .Where(g => OperatorPayoutEligibilityReader.IsMonthClosed(g.Key, nowUtc))
            .OrderBy(g => g.Key)
            .ToList();

        if (closedMonthGroups.Count == 0)
        {
            return Result.Failure<PayoutSummaryDto>(
                OperatorPayoutErrorCodes.NoEligibleBooking, "No eligible completed bookings were found in any closed settlement month.");
        }

        var allBookingIds = closedMonthGroups.SelectMany(g => g.Select(x => x.Booking.BookingId)).ToList();
        var allRefunds = await reader.LoadProcessedRefundsAsync(allBookingIds, ct);
        var refundsByBooking = allRefunds.ToLookup(r => r.BookingId);

        if (closedMonthGroups.Any(g => g.Any(x =>
                refundsByBooking[x.Booking.BookingId].Sum(r => r.RefundedAmount) > x.Booking.PaidAmount)))
        {
            // Refund-exceeds-payment is a data fault (UC-46 BE spec rule 6), never silently
            // clamped into a payout. Surfaced as a system error so it gets investigated.
            return Result.Failure<PayoutSummaryDto>(
                OperatorPayoutErrorCodes.SystemError, "A booking's refunds exceed its payment and cannot be settled automatically.");
        }

        var monthCalculations = new List<(DateOnly Month, List<PayoutPaymentInput> Payments, List<PayoutRefundInput> Refunds, OperatorPayoutCalculationResult Calc)>();
        foreach (var group in closedMonthGroups)
        {
            var bookingIds = group.Select(x => x.Booking.BookingId).ToHashSet();
            var payments = group.Select(x => new PayoutPaymentInput(
                x.Booking.BookingId, x.Booking.PaymentTransactionId, x.Booking.PaidAmount, x.Booking.DiscountAmount,
                x.Booking.CommissionRateApplied, x.Booking.ParticipantCount)).ToList();
            var refunds = allRefunds.Where(r => bookingIds.Contains(r.BookingId)).ToList();
            var calc = OperatorPayoutCalculator.Calculate(payments, refunds, []);
            monthCalculations.Add((group.Key, payments, refunds, calc));
        }

        var totalPayableBeforeAdjustments = monthCalculations.Sum(m => m.Calc.PayableNetBeforeAdjustments);
        if (totalPayableBeforeAdjustments < 0)
        {
            return Result.Failure<PayoutSummaryDto>(
                OperatorPayoutErrorCodes.PayoutNotRequestable, "The combined payable amount across all outstanding months is negative.");
        }

        // Adjustments are consumed as whole ledger rows only (oldest first, never split — see
        // UC-46 BE spec v3 "adjustments" rule), applied against the grand total across all months.
        var outstandingAdjustments = await db.PayoutAdjustments
            .Where(a => a.OperatorUserId == operatorId && a.AppliedPayoutId == null)
            .OrderBy(a => a.CreatedAtUtc)
            .ToListAsync(ct);

        var includedAdjustments = new List<PayoutAdjustment>();
        var remainingPayable = totalPayableBeforeAdjustments;
        foreach (var adjustment in outstandingAdjustments)
        {
            var absAmount = -adjustment.Amount;
            if (absAmount > remainingPayable) break;
            includedAdjustments.Add(adjustment);
            remainingPayable -= absAmount;
        }
        var adjustmentsApplied = includedAdjustments.Sum(a => -a.Amount);
        var totalPayable = totalPayableBeforeAdjustments - adjustmentsApplied;

        if (totalPayable < minimumAmount)
        {
            return Result.Failure<PayoutSummaryDto>(
                OperatorPayoutErrorCodes.BelowMinimumPayout, "The combined payable amount is below the configured minimum payout.");
        }

        return await db.ExecuteInSerializableTransactionAsync(async innerCt =>
        {
            var monthTotals = monthCalculations.Select(m => new PayoutMonthTotal(
                m.Month,
                m.Payments.Count,
                m.Payments.Sum(p => p.ParticipantCount),
                m.Calc.PaymentsReceived,
                m.Calc.DiscountsGiven,
                m.Calc.RefundedAmount,
                m.Calc.CommissionAmount,
                m.Calc.PayableNetBeforeAdjustments)).ToList();

            var payout = Payout.Create(
                payoutCode: $"TMP-{Guid.NewGuid():N}"[..Payout.PayoutCodeMaxLength], // unique placeholder; final code assigned after insert
                operatorUserId: operatorId,
                months: monthTotals,
                cutOffAtUtc: nowUtc,
                adjustmentsApplied: adjustmentsApplied,
                minimumAmountChecked: minimumAmount,
                bankName: operatorProfile.BankName!,
                accountHolderName: operatorProfile.AccountHolderName!,
                accountNumberMasked: operatorProfile.AccountNumberMasked!,
                accountNumberProtected: operatorProfile.AccountNumberProtected!,
                requestedAtUtc: nowUtc);

            foreach (var month in monthCalculations)
            {
                foreach (var item in month.Calc.Items)
                {
                    payout.AddItem(PayoutItem.Create(
                        item.BookingId, item.PaymentTransactionId, item.PaidAmount,
                        item.DiscountAmount, item.CommissionRateApplied, item.CommissionAmount));
                }
                foreach (var refundItem in month.Calc.RefundItems)
                {
                    payout.AddRefundItem(PayoutRefundItem.Create(
                        refundItem.RefundId, refundItem.BookingId, refundItem.RefundedAmount,
                        refundItem.CommissionRateApplied, refundItem.CommissionReduction));
                }
            }

            db.Payouts.Add(payout);

            try
            {
                await db.SaveChangesAsync(innerCt);
            }
            catch (DbUpdateException exception) when (persistenceErrors.IsActiveOperatorConflict(exception))
            {
                return Result.Failure<PayoutSummaryDto>(
                    OperatorPayoutErrorCodes.ActivePayoutExists, "A payout request is already in progress for this operator.");
            }

            payout.AssignPayoutCode($"{Payout.PayoutCodePrefix}{nowUtc:yyyyMM}-{payout.Id:D6}");

            foreach (var adjustment in includedAdjustments)
            {
                adjustment.Apply(payout.Id, adjustment.Amount, nowUtc);
            }

            await db.SaveChangesAsync(innerCt);

            var monthDtos = payout.PeriodBreakdowns
                .OrderBy(b => b.SettlementMonth)
                .Select(b => new PayoutMonthBreakdownDto(
                    b.SettlementMonth.Year, b.SettlementMonth.Month, b.EligibleBookingCount,
                    b.PaymentsReceived, b.DiscountsGiven, b.RefundedAmount, b.CommissionAmount, b.PayableNetAmount))
                .ToList();

            return Result.Success(new PayoutSummaryDto(
                payout.PayoutCode, payout.Status, payout.PeriodCount,
                payout.PeriodStart.ToString("yyyy-MM-dd"), payout.PeriodEnd.ToString("yyyy-MM-dd"),
                payout.PaymentsReceived, payout.DiscountsGiven, payout.RefundedAmount, payout.NetSales,
                payout.CommissionAmount, payout.AdjustmentsApplied, payout.NetAmount, payout.RequestedAtUtc!.Value,
                payout.BankName!, payout.AccountHolderName!, payout.AccountNumberMasked!, monthDtos));
        }, ct);
    }
}