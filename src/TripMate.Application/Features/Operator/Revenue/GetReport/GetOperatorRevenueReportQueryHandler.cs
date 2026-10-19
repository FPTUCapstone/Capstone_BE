using MediatR;

using Microsoft.EntityFrameworkCore;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Common.Models;
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
        var refundedAmount = await db.Refunds.AsNoTracking()
            .Where(refund => refund.Status == "Processed" && refund.ProcessedAtUtc >= from && refund.ProcessedAtUtc < until)
            .Where(refund => db.Bookings.Any(booking => booking.Id == refund.BookingId &&
                booking.TourSchedule.Tour.OperatorUserId == operatorId &&
                (!request.TourId.HasValue || booking.TourSchedule.TourId == request.TourId.Value)))
            .SumAsync(refund => (decimal?)refund.Amount, ct) ?? 0m;
        var summary = new OperatorRevenueSummaryDto(
            canonical.Sum(x => x.TotalAmount + x.DiscountAmount), canonical.Sum(x => x.Amount), canonical.Sum(x => x.DiscountAmount), refundedAmount,
            canonical.Sum(x => x.Amount) - refundedAmount, canonical.Sum(x => Math.Round(x.Amount * x.Rate / 100m, 2)), canonical.Sum(x => x.Amount - Math.Round(x.Amount * x.Rate / 100m, 2)) - refundedAmount, canonical.Count, canonical.Sum(x => x.Quantity), canonical.Count == 0 ? 0m : canonical.Average(x => x.Rate));
        var details = canonical.GroupBy(x => new { x.TourId, x.Title }).Select(g => new OperatorTourRevenueDto(g.Key.TourId, $"TOUR-{g.Key.TourId}", g.Key.Title, g.Count(), g.Sum(x => x.Quantity), g.Sum(x => x.TotalAmount + x.DiscountAmount), 0m, g.Sum(x => Math.Round(x.Amount * x.Rate / 100m, 2)), g.Sum(x => x.Amount - Math.Round(x.Amount * x.Rate / 100m, 2)))).OrderByDescending(x => x.NetAmount).ToList();
        var chart = canonical.GroupBy(x => PeriodKey(x.PaidAt, request.Granularity, zone)).OrderBy(g => g.Key).Select(g => new OperatorRevenueChartDto(g.Key, g.Key, g.Sum(x => x.TotalAmount + x.DiscountAmount), g.Sum(x => Math.Round(x.Amount * x.Rate / 100m, 2)), g.Sum(x => x.Amount - Math.Round(x.Amount * x.Rate / 100m, 2)), 0m, g.Count())).ToList();
        return Result.Success(new OperatorRevenueReportResponse(summary, chart, details.Skip((page - 1) * pageSize).Take(pageSize).ToList(), details.Count, page, pageSize, details.Count == 0 ? 0 : (int)Math.Ceiling(details.Count / (double)pageSize)));
    }
    private static string PeriodKey(DateTimeOffset utc, string granularity, TimeZoneInfo zone) { var d = TimeZoneInfo.ConvertTime(utc, zone); return granularity == "daily" ? d.ToString("yyyy-MM-dd") : granularity == "weekly" ? $"{d:yyyy}-W{System.Globalization.ISOWeek.GetWeekOfYear(d.DateTime):D2}" : d.ToString("yyyy-MM"); }
    private static TimeZoneInfo FindVietnamTimeZone() { try { return TimeZoneInfo.FindSystemTimeZoneById("SE Asia Standard Time"); } catch (TimeZoneNotFoundException) { return TimeZoneInfo.FindSystemTimeZoneById("Asia/Ho_Chi_Minh"); } }
}