using MediatR;

using Microsoft.EntityFrameworkCore;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Common.Models;
using TripMate.Application.Features.Operator.Revenue.Common;
using TripMate.Application.Features.Operator.Tours.Common;
using TripMate.Domain.Enums;

namespace TripMate.Application.Features.Operator.Revenue.GetReport;

public sealed class GetOperatorRevenueReportQueryHandler(IApplicationDbContext db, ICurrentUserService currentUser)
    : IRequestHandler<GetOperatorRevenueReportQuery, Result<OperatorRevenueReportResponse>>
{
    public async Task<Result<OperatorRevenueReportResponse>> Handle(GetOperatorRevenueReportQuery request, CancellationToken ct)
    {
        if (currentUser.UserId is not long operatorId || currentUser.Role != nameof(UserRole.TourOperator) || request.EndDate < request.StartDate)
            return Result.Failure<OperatorRevenueReportResponse>(OperatorTourErrorCodes.AccessRequired, "An active approved Tour Operator account is required.");

        // P2: Validate granularity parameter
        if (!IsValidGranularity(request.Granularity))
            return Result.Failure<OperatorRevenueReportResponse>(OperatorRevenueErrorCodes.InvalidGranularity, "Granularity must be 'daily', 'weekly', or 'monthly'.");

        var active = await db.OperatorProfiles.AnyAsync(p => p.UserId == operatorId && p.ApprovalStatus == OperatorApprovalStatus.Approved && p.User.Status == AccountStatus.Active, ct);
        if (!active) return Result.Failure<OperatorRevenueReportResponse>(OperatorTourErrorCodes.AccessRequired, "An active approved Tour Operator account is required.");

        var zone = FindVietnamTimeZone();
        var from = new DateTimeOffset(request.StartDate.ToDateTime(TimeOnly.MinValue), zone.GetUtcOffset(request.StartDate.ToDateTime(TimeOnly.MinValue))).ToUniversalTime();
        var untilDate = request.EndDate.AddDays(1);
        var until = new DateTimeOffset(untilDate.ToDateTime(TimeOnly.MinValue), zone.GetUtcOffset(untilDate.ToDateTime(TimeOnly.MinValue))).ToUniversalTime();
        int page = Math.Max(1, request.Page), pageSize = Math.Clamp(request.PageSize, 1, 100);

        var rows = await db.PaymentTransactions.AsNoTracking()
            .Where(p => p.Status == PaymentTransactionStatus.Success && (p.Booking.Status == BookingStatus.Confirmed || p.Booking.Status == BookingStatus.Completed) && p.Booking.PaymentStatus == PaymentStatus.Paid && (p.SucceededAtUtc ?? p.CreatedAtUtc) >= from && (p.SucceededAtUtc ?? p.CreatedAtUtc) < until && p.Booking.TourSchedule.Tour.OperatorUserId == operatorId && (!request.TourId.HasValue || p.Booking.TourSchedule.TourId == request.TourId.Value))
            .Select(p => new { p.BookingId, p.Amount, p.Booking.TotalAmount, p.Booking.DiscountAmount, p.Booking.Quantity, TourId = p.Booking.TourSchedule.TourId, p.Booking.TourSchedule.Tour.Title, p.Booking.TourSchedule.Tour.CreatedAtUtc, PaidAt = p.SucceededAtUtc ?? p.CreatedAtUtc, Rate = p.CommissionRateApplied ?? db.OperatorProfiles.Where(o => o.UserId == operatorId).Select(o => o.CommissionRate).FirstOrDefault() })
            .ToListAsync(ct);

        var canonical = rows.GroupBy(x => x.BookingId).Select(g => g.OrderBy(x => x.PaidAt).First()).ToList();

        // P1: Get refunds grouped by TourId and PeriodKey (not just total sum)
        var refundsData = await db.Refunds.AsNoTracking()
            .Join(db.Bookings.AsNoTracking(), refund => refund.BookingId, booking => booking.Id, (refund, booking) => new { refund, booking })
            .Where(x => x.refund.Status == RefundStatus.Processed && x.refund.ProcessedAtUtc >= from && x.refund.ProcessedAtUtc < until
                && x.booking.TourSchedule.Tour.OperatorUserId == operatorId
                && (!request.TourId.HasValue || x.booking.TourSchedule.TourId == request.TourId.Value))
            .Select(x => new { x.refund.BookingId, x.refund.Amount, ProcessedAtUtc = x.refund.ProcessedAtUtc!.Value, TourId = x.booking.TourSchedule.TourId })
            .ToListAsync(ct);

        var totalRefundedAmount = refundsData.Sum(x => x.Amount);

        // Build refund map: (TourId, PeriodKey) -> RefundedAmount
        var refundsByTourAndPeriod = refundsData
            .GroupBy(x => new { x.TourId, PeriodKey = PeriodKey(x.ProcessedAtUtc, request.Granularity, zone) })
            .ToDictionary(g => g.Key, g => g.Sum(x => x.Amount));

        var summary = new OperatorRevenueSummaryDto(
            canonical.Sum(x => x.TotalAmount + x.DiscountAmount), canonical.Sum(x => x.Amount), canonical.Sum(x => x.DiscountAmount), totalRefundedAmount,
            canonical.Sum(x => x.Amount) - totalRefundedAmount, canonical.Sum(x => Math.Round(x.Amount * x.Rate / 100m, 2)), canonical.Sum(x => x.Amount - Math.Round(x.Amount * x.Rate / 100m, 2)) - totalRefundedAmount, canonical.Count, canonical.Sum(x => x.Quantity), canonical.Count == 0 ? 0m : canonical.Average(x => x.Rate));

        var details = canonical.GroupBy(x => new { x.TourId, x.Title }).Select(g =>
        {
            var refundedForTour = refundsData.Where(r => r.TourId == g.Key.TourId).Sum(r => r.Amount);
            var gross = g.Sum(x => x.TotalAmount + x.DiscountAmount);
            var commission = g.Sum(x => Math.Round(x.Amount * x.Rate / 100m, 2));
            var net = g.Sum(x => x.Amount - Math.Round(x.Amount * x.Rate / 100m, 2)) - refundedForTour;
            return new OperatorTourRevenueDto(g.Key.TourId, $"TOUR-{g.Key.TourId}", g.Key.Title, g.Count(), g.Sum(x => x.Quantity), gross, refundedForTour, commission, net);
        }).OrderByDescending(x => x.NetAmount).ToList();

        var chart = canonical.GroupBy(x => PeriodKey(x.PaidAt, request.Granularity, zone)).OrderBy(g => g.Key).Select(g =>
        {
            var refundedInPeriod = refundsByTourAndPeriod
                .Where(kvp => kvp.Key.PeriodKey == g.Key)
                .Sum(kvp => kvp.Value);
            var gross = g.Sum(x => x.TotalAmount + x.DiscountAmount);
            var commission = g.Sum(x => Math.Round(x.Amount * x.Rate / 100m, 2));
            var net = g.Sum(x => x.Amount - Math.Round(x.Amount * x.Rate / 100m, 2)) - refundedInPeriod;
            return new OperatorRevenueChartDto(g.Key, g.Key, gross, commission, net, refundedInPeriod, g.Count());
        }).ToList();

        return Result.Success(new OperatorRevenueReportResponse(summary, chart, details.Skip((page - 1) * pageSize).Take(pageSize).ToList(), details.Count, page, pageSize, details.Count == 0 ? 0 : (int)Math.Ceiling(details.Count / (double)pageSize)));
    }

    private static bool IsValidGranularity(string granularity) => granularity is "daily" or "weekly" or "monthly";
    private static string PeriodKey(DateTimeOffset utc, string granularity, TimeZoneInfo zone) { var d = TimeZoneInfo.ConvertTime(utc, zone); return granularity == "daily" ? d.ToString("yyyy-MM-dd") : granularity == "weekly" ? $"{d:yyyy}-W{System.Globalization.ISOWeek.GetWeekOfYear(d.DateTime):D2}" : d.ToString("yyyy-MM"); }
    private static TimeZoneInfo FindVietnamTimeZone() { try { return TimeZoneInfo.FindSystemTimeZoneById("SE Asia Standard Time"); } catch (TimeZoneNotFoundException) { return TimeZoneInfo.FindSystemTimeZoneById("Asia/Ho_Chi_Minh"); } }
}