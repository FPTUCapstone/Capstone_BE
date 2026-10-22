using Microsoft.EntityFrameworkCore;

using TripMate.Application.Common.Interfaces;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Application.Features.Operator.Payouts.Common;

public sealed record EligibleBookingRow(
    long BookingId,
    long PaymentTransactionId,
    decimal PaidAmount,
    decimal DiscountAmount,
    decimal CommissionRateApplied,
    int ParticipantCount,
    DateTimeOffset PayoutEligibleAtUtc);

/// <summary>
/// Loads the UC-46 settlement booking set for an operator: Completed bookings, with a verified
/// payment, whose tour schedule ended at least <c>refundHoldDays</c> ago (UC-46 BE spec OPEN-1,
/// a placeholder value pending PO confirmation), and that are not already part of an active or
/// settled payout (BR-113 / "a booking enters at most one payout").
///
/// Simplification vs. the full spec: <c>payoutEligibleAtUtc</c> is computed here from the tour
/// schedule's end time at query time, not stored as an immutable snapshot on Booking at creation
/// (which would require touching the UC-27 booking-creation flow). Because a tour schedule's
/// EndAtUtc does not change after creation in the current codebase, this is behaviourally
/// equivalent for now; it should be revisited if schedules ever become reschedulable.
///
/// Known gap: a refund that completes against a booking AFTER that booking's payout was already
/// submitted does not yet create a <see cref="PayoutAdjustment"/> automatically (UC-46 BE spec
/// rule 8). That booking is excluded from this reader (it is already "consumed"), so no refund
/// of it is ever double counted — but the adjustment ledger entry itself must be created by a
/// follow-up hook in the refund-processing flow, not yet implemented.
/// </summary>
public sealed class OperatorPayoutEligibilityReader(IApplicationDbContext db)
{
    public static readonly TimeZoneInfo VietnamTimeZone = FindVietnamTimeZone();

    public async Task<IReadOnlyList<EligibleBookingRow>> LoadEligibleBookingsAsync(
        long operatorId,
        DateTimeOffset nowUtc,
        int refundHoldDays,
        CancellationToken ct)
    {
        var consumedBookingIds = await db.PayoutItems
            .AsNoTracking()
            .Where(item => db.Payouts.Any(p => p.Id == item.PayoutId
                && p.Status != Payout.StatusRejected
                && p.Status != Payout.StatusCancelled))
            .Select(item => item.BookingId)
            .ToListAsync(ct);
        var consumed = consumedBookingIds.ToHashSet();

        var rows = await db.PaymentTransactions.AsNoTracking()
            .Where(p => p.Status == PaymentTransactionStatus.Success
                && p.Booking.Status == BookingStatus.Completed
                && p.Booking.PaymentStatus == PaymentStatus.Paid
                && p.Booking.TourSchedule.Tour.OperatorUserId == operatorId)
            .Select(p => new
            {
                p.BookingId,
                PaymentTransactionId = p.Id,
                p.Amount,
                p.Booking.DiscountAmount,
                p.Booking.Quantity,
                PaidAt = p.SucceededAtUtc ?? p.CreatedAtUtc,
                p.Booking.TourSchedule.EndAtUtc,
                Rate = p.CommissionRateApplied ?? db.OperatorProfiles.Where(o => o.UserId == operatorId).Select(o => o.CommissionRate).FirstOrDefault(),
            })
            .ToListAsync(ct);

        // One canonical payment per booking (earliest success), same rule as UC-44/UC-45.
        var canonical = rows
            .GroupBy(x => x.BookingId)
            .Select(g => g.OrderBy(x => x.PaidAt).First())
            .Where(x => !consumed.Contains(x.BookingId))
            .ToList();

        var result = new List<EligibleBookingRow>(canonical.Count);
        foreach (var row in canonical)
        {
            var eligibleAtUtc = row.EndAtUtc.AddDays(refundHoldDays);
            if (eligibleAtUtc > nowUtc) continue; // still inside the hold

            result.Add(new EligibleBookingRow(
                row.BookingId, row.PaymentTransactionId, row.Amount, row.DiscountAmount,
                row.Rate, row.Quantity, eligibleAtUtc));
        }

        return result;
    }

    /// <summary>Processed refunds for the given bookings, for feeding into the calculator alongside
    /// their payments. Only bookings already known to be eligible/owned are passed in.</summary>
    public async Task<IReadOnlyList<PayoutRefundInput>> LoadProcessedRefundsAsync(
        IReadOnlyCollection<long> bookingIds, CancellationToken ct)
    {
        if (bookingIds.Count == 0) return [];

        return await db.Refunds.AsNoTracking()
            .Where(r => r.Status == RefundStatus.Processed && bookingIds.Contains(r.BookingId))
            .Select(r => new PayoutRefundInput(
                r.Id,
                r.BookingId,
                r.Amount,
                // The refund reverses whichever payment it targets; falling back to the booking's
                // single canonical payment rate keeps this aligned with the calculator/UC-44 rule.
                db.PaymentTransactions.Where(p => p.BookingId == r.BookingId && p.Status == PaymentTransactionStatus.Success)
                    .OrderBy(p => p.SucceededAtUtc ?? p.CreatedAtUtc)
                    .Select(p => p.CommissionRateApplied ?? 0m)
                    .FirstOrDefault()))
            .ToListAsync(ct);
    }

    public async Task<decimal> LoadDecimalConfigAsync(string key, decimal defaultValue, CancellationToken ct)
    {
        var raw = await db.SystemConfigs.AsNoTracking()
            .Where(c => c.ConfigKey == key)
            .Select(c => c.ConfigValue)
            .FirstOrDefaultAsync(ct);
        return raw is not null && decimal.TryParse(raw, out var value) ? value : defaultValue;
    }

    public async Task<int> LoadIntConfigAsync(string key, int defaultValue, CancellationToken ct)
    {
        var raw = await db.SystemConfigs.AsNoTracking()
            .Where(c => c.ConfigKey == key)
            .Select(c => c.ConfigValue)
            .FirstOrDefaultAsync(ct);
        return raw is not null && int.TryParse(raw, out var value) ? value : defaultValue;
    }

    public static DateOnly SettlementMonthOf(DateTimeOffset payoutEligibleAtUtc)
    {
        var local = TimeZoneInfo.ConvertTime(payoutEligibleAtUtc, VietnamTimeZone);
        return new DateOnly(local.Year, local.Month, 1);
    }

    /// <summary>A settlement month is closed once Vietnam-local "now" has reached the first day
    /// of the following month — i.e. the month has fully elapsed.</summary>
    public static bool IsMonthClosed(DateOnly settlementMonth, DateTimeOffset nowUtc)
    {
        var nowLocal = TimeZoneInfo.ConvertTime(nowUtc, VietnamTimeZone);
        var nowDate = DateOnly.FromDateTime(nowLocal.DateTime);
        var nextMonthStart = new DateOnly(settlementMonth.Year, settlementMonth.Month, 1).AddMonths(1);
        return nowDate >= nextMonthStart;
    }

    private static TimeZoneInfo FindVietnamTimeZone()
    {
        try { return TimeZoneInfo.FindSystemTimeZoneById("SE Asia Standard Time"); }
        catch (TimeZoneNotFoundException) { return TimeZoneInfo.FindSystemTimeZoneById("Asia/Ho_Chi_Minh"); }
    }
}
