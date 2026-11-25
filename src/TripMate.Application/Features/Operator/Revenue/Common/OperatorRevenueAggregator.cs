using Microsoft.EntityFrameworkCore;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Features.Operator.Revenue.GetReport;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Application.Features.Operator.Revenue.Common;

/// <summary>
/// The single commission/net calculation path shared by UC-44 (view, paginated) and UC-45
/// (export, unpaginated) revenue reporting (SRS §3.8.5.1, Option B commission policy). Nothing
/// here recomputes a divergent figure: both features read the same confirmed/completed,
/// verified-payment bookings and the same historical commission snapshot, so a displayed total
/// and an exported total can never disagree.
/// </summary>
public sealed class OperatorRevenueAggregator(IApplicationDbContext db)
{
    private const decimal CancellationFeePlatformCommissionPercent = 10m;

    public async Task<OperatorRevenueAggregate> AggregateAsync(
        long operatorId,
        DateOnly startDate,
        DateOnly endDate,
        string granularity,
        long? tourId,
        CancellationToken ct)
    {
        var zone = FindVietnamTimeZone();
        var from = new DateTimeOffset(startDate.ToDateTime(TimeOnly.MinValue), zone.GetUtcOffset(startDate.ToDateTime(TimeOnly.MinValue))).ToUniversalTime();
        var untilDate = endDate.AddDays(1);
        var until = new DateTimeOffset(untilDate.ToDateTime(TimeOnly.MinValue), zone.GetUtcOffset(untilDate.ToDateTime(TimeOnly.MinValue))).ToUniversalTime();

        var rows = await db.PaymentTransactions.AsNoTracking()
            .Where(p => p.TransactionType == PaymentTransaction.TypePayment
                && p.Status == PaymentTransactionStatus.Success
                && ((p.Booking.Status == BookingStatus.Confirmed || p.Booking.Status == BookingStatus.Completed)
                    && p.Booking.PaymentStatus == PaymentStatus.Paid
                    || p.Booking.Status == BookingStatus.Cancelled
                    && (p.Booking.PaymentStatus == PaymentStatus.PartiallyRefunded
                        || p.Booking.PaymentStatus == PaymentStatus.Refunded
                        // Do not erase cash already sent to an operator while a cancellation is
                        // awaiting the customer-refund/reconciliation outcome. Once the Advance
                        // payout exists, the later return receipt is what reduces NetAmount.
                        || p.Booking.PaymentStatus == PaymentStatus.Paid
                        && db.PayoutSettlements.Any(settlement => settlement.BookingId == p.BookingId
                            && settlement.Status == PayoutSettlementStatus.Settled)))
                && (p.SucceededAtUtc ?? p.CreatedAtUtc) >= from
                && (p.SucceededAtUtc ?? p.CreatedAtUtc) < until
                && p.Booking.TourSchedule.Tour.OperatorUserId == operatorId
                && (!tourId.HasValue || p.Booking.TourSchedule.TourId == tourId.Value))
            .Select(p => new
            {
                p.BookingId,
                p.Amount,
                p.Booking.TotalAmount,
                p.Booking.DiscountAmount,
                p.Booking.Quantity,
                BookingStatus = p.Booking.Status,
                TourId = p.Booking.TourSchedule.TourId,
                p.Booking.TourSchedule.Tour.Title,
                PaidAt = p.SucceededAtUtc ?? p.CreatedAtUtc,
                Rate = p.CommissionRateApplied
                    ?? db.OperatorProfiles.Where(o => o.UserId == operatorId).Select(o => o.CommissionRate).FirstOrDefault(),
            })
            .ToListAsync(ct);

        var canonical = rows.GroupBy(x => x.BookingId).Select(g => g.OrderBy(x => x.PaidAt).First()).ToList();

        var bookingIds = canonical.Select(x => x.BookingId).ToList();

        // Option B commission policy: a refund offsets the commission/net once it has either
        // reached the customer or the operator's return has been confirmed by TripMate. The
        // latter matters when the gateway is retrying: the operator's cash position has already
        // changed and the report must not show a zero refund alongside a reduced Net Amount.
        // Refunds are looked up by booking id only (no ProcessedAtUtc filter against the report window) — otherwise
        // a booking paid in one period but refunded in a later period would show full commission
        // in BOTH periods' reports (the refund's own period has no matching payment to apply it
        // against, and the payment's period never learns about a refund that hasn't happened yet).
        var refundsByBooking = bookingIds.Count == 0
            ? new Dictionary<long, decimal>()
            : await db.Refunds.AsNoTracking()
                .Where(r => bookingIds.Contains(r.BookingId)
                    && (r.Status == RefundStatus.Processed || r.ReturnReceivedAtUtc != null))
                .GroupBy(r => r.BookingId)
                .Select(g => new { BookingId = g.Key, Amount = g.Sum(x => x.Amount) })
                .ToDictionaryAsync(x => x.BookingId, x => x.Amount, ct);

        // "Net Amount" is the Tour Operator's realised cash position, never merely the
        // value of a confirmed customer booking. A new booking contributes zero until an
        // Advance/Final/Cancellation settlement is actually settled. Any bank return from the
        // operator is deducted only after Staff has confirmed receipt in TripMate's account.
        var settledPayoutByBooking = bookingIds.Count == 0
            ? new Dictionary<long, decimal>()
            : await db.PayoutSettlements.AsNoTracking()
                .Where(settlement => bookingIds.Contains(settlement.BookingId)
                    && settlement.Status == PayoutSettlementStatus.Settled)
                .GroupBy(settlement => settlement.BookingId)
                .Select(group => new { BookingId = group.Key, Amount = group.Sum(x => x.PayoutAmount) })
                .ToDictionaryAsync(x => x.BookingId, x => x.Amount, ct);

        var receivedReturnByBooking = bookingIds.Count == 0
            ? new Dictionary<long, decimal>()
            : await db.Refunds.AsNoTracking()
                .Where(refund => bookingIds.Contains(refund.BookingId)
                    && refund.ReturnReceivedAtUtc != null
                    && refund.OperatorReturnShare != null)
                .GroupBy(refund => refund.BookingId)
                .Select(group => new { BookingId = group.Key, Amount = group.Sum(x => x.OperatorReturnShare!.Value) })
                .ToDictionaryAsync(x => x.BookingId, x => x.Amount, ct);

        var totalRefundedAmount = refundsByBooking.Values.Sum();

        // Commission remains an accounting allocation on the paid booking. It is deliberately
        // separate from NetAmount, which is cash realised through settled payouts.
        decimal CommissionFor(long bookingId, decimal amount, decimal rate, BookingStatus bookingStatus)
        {
            var refunded = refundsByBooking.GetValueOrDefault(bookingId, 0m);
            var net = Math.Max(0m, amount - refunded);
            if (bookingStatus == BookingStatus.Cancelled)
                return Math.Round(net * CancellationFeePlatformCommissionPercent / 100m, 2);

            // Commission snapshots created by the payment flow are stored as fractions
            // (0.10 = 10%). Older records/tests may contain percent points (10 = 10%),
            // so normalise both representations at the reporting boundary.
            var fraction = rate is > 0m and <= 1m ? rate : rate / 100m;
            return Math.Round(net * fraction, 2);
        }

        decimal NetAmountFor(long bookingId) =>
            settledPayoutByBooking.GetValueOrDefault(bookingId, 0m)
            - receivedReturnByBooking.GetValueOrDefault(bookingId, 0m);

        var paymentsReceived = canonical.Sum(x => x.Amount);
        var discountsGiven = canonical.Sum(x => x.DiscountAmount);
        var platformCommission = canonical.Sum(x => CommissionFor(x.BookingId, x.Amount, x.Rate, x.BookingStatus));
        var netSales = paymentsReceived - totalRefundedAmount;
        var netAmount = canonical.Sum(x => NetAmountFor(x.BookingId));

        var summary = new OperatorRevenueSummaryDto(
            canonical.Sum(x => x.TotalAmount + x.DiscountAmount),
            paymentsReceived,
            discountsGiven,
            totalRefundedAmount,
            netSales,
            platformCommission,
            netAmount,
            canonical.Count,
            canonical.Sum(x => x.Quantity),
            canonical.Count == 0 ? 0m : canonical.Average(x => x.Rate is > 0m and <= 1m ? x.Rate * 100m : x.Rate));

        var allTourDetails = canonical.GroupBy(x => new { x.TourId, x.Title }).Select(g =>
        {
            var refundedForTour = g.Sum(x => refundsByBooking.GetValueOrDefault(x.BookingId, 0m));
            var gross = g.Sum(x => x.TotalAmount + x.DiscountAmount);
            var commission = g.Sum(x => CommissionFor(x.BookingId, x.Amount, x.Rate, x.BookingStatus));
            var net = g.Sum(x => NetAmountFor(x.BookingId));
            return new OperatorTourRevenueDto(g.Key.TourId, $"TOUR-{g.Key.TourId}", g.Key.Title, g.Count(), g.Sum(x => x.Quantity), gross, refundedForTour, commission, net);
        }).OrderByDescending(x => x.NetAmount).ToList();

        var chartData = canonical.GroupBy(x => PeriodKey(x.PaidAt, granularity, zone)).OrderBy(g => g.Key).Select(g =>
        {
            var refundedInPeriod = g.Sum(x => refundsByBooking.GetValueOrDefault(x.BookingId, 0m));
            var gross = g.Sum(x => x.TotalAmount + x.DiscountAmount);
            var commission = g.Sum(x => CommissionFor(x.BookingId, x.Amount, x.Rate, x.BookingStatus));
            var net = g.Sum(x => NetAmountFor(x.BookingId));
            return new OperatorRevenueChartDto(g.Key, g.Key, gross, commission, net, refundedInPeriod, g.Count());
        }).ToList();

        return new OperatorRevenueAggregate(summary, chartData, allTourDetails);
    }

    public static bool IsValidGranularity(string granularity) => granularity is "daily" or "weekly" or "monthly";

    private static string PeriodKey(DateTimeOffset utc, string granularity, TimeZoneInfo zone)
    {
        var d = TimeZoneInfo.ConvertTime(utc, zone);
        return granularity == "daily" ? d.ToString("yyyy-MM-dd")
            : granularity == "weekly" ? $"{d:yyyy}-W{System.Globalization.ISOWeek.GetWeekOfYear(d.DateTime):D2}"
            : d.ToString("yyyy-MM");
    }

    private static TimeZoneInfo FindVietnamTimeZone()
    {
        try { return TimeZoneInfo.FindSystemTimeZoneById("SE Asia Standard Time"); }
        catch (TimeZoneNotFoundException) { return TimeZoneInfo.FindSystemTimeZoneById("Asia/Ho_Chi_Minh"); }
    }
}

/// <summary>Unpaginated aggregation result: the full tour-detail set, already sorted by Net Amount descending.</summary>
public sealed record OperatorRevenueAggregate(
    OperatorRevenueSummaryDto Summary,
    List<OperatorRevenueChartDto> ChartData,
    List<OperatorTourRevenueDto> AllTourDetails);