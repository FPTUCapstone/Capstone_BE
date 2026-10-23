using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

using MediatR;

using Microsoft.EntityFrameworkCore;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Common.Models;
using TripMate.Application.Features.Admin.StatisticalReports.Common;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Application.Features.Admin.StatisticalReports.GetReport;

/// <summary>
/// UC-67 statistical report generator. A request is accepted only for an already closed calendar
/// period. The first successful result for its exact criteria is persisted as a snapshot; later
/// views and exports read that same payload rather than recalculating mutable booking data.
/// </summary>
public sealed class GetStatisticalReportQueryHandler(
    IApplicationDbContext db,
    ICurrentUserService currentUser,
    IDateTimeProvider clock)
    : IRequestHandler<GetStatisticalReportQuery, Result<StatisticalReportDto>>
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<Result<StatisticalReportDto>> Handle(GetStatisticalReportQuery request, CancellationToken ct)
    {
        if (currentUser.UserId is null || currentUser.Role != nameof(UserRole.Administrator))
            return Result.Failure<StatisticalReportDto>(StatisticalReportErrorCodes.Forbidden, "Administrator access is required.");

        string reportType = request.ReportType?.Trim().ToUpperInvariant() ?? string.Empty;
        string bookingType = request.BookingType?.Trim().ToUpperInvariant() ?? string.Empty;
        string periodKey = request.PeriodKey?.Trim().ToUpperInvariant() ?? string.Empty;
        if (!StatisticalReportValues.ReportTypes.Contains(reportType) ||
            !StatisticalReportValues.BookingTypes.Contains(bookingType) || string.IsNullOrWhiteSpace(periodKey))
        {
            return Result.Failure<StatisticalReportDto>(StatisticalReportErrorCodes.InvalidCriteria,
                "The report type, booking type, or reporting period is invalid.");
        }

        var scopeError = ValidateScope(reportType, bookingType, request.OperatorUserId, request.DestinationId);
        if (scopeError is not null)
            return Result.Failure<StatisticalReportDto>(StatisticalReportErrorCodes.UnsupportedScope, scopeError);

        ClosedStatisticalPeriod? period = await db.ClosedStatisticalPeriods.AsNoTracking()
            .SingleOrDefaultAsync(x => x.PeriodKey == periodKey, ct);
        if (period is null)
            return Result.Failure<StatisticalReportDto>(StatisticalReportErrorCodes.PeriodNotClosed,
                "The selected reporting period is not closed.");

        string criteriaKey = CreateCriteriaKey(reportType, periodKey, bookingType, request.OperatorUserId, request.DestinationId);
        StatisticalReportSnapshot? persisted = await db.StatisticalReportSnapshots.AsNoTracking()
            .SingleOrDefaultAsync(x => x.CriteriaKey == criteriaKey, ct);
        if (persisted is not null)
            return Deserialize(persisted.PayloadJson);

        StatisticalReportDto generated = await BuildAsync(reportType, bookingType, request.OperatorUserId,
            request.DestinationId, ToDto(period), ct);
        string payload = JsonSerializer.Serialize(generated, JsonOptions);
        db.StatisticalReportSnapshots.Add(StatisticalReportSnapshot.Create(criteriaKey, periodKey, reportType,
            bookingType, request.OperatorUserId, request.DestinationId, payload, clock.UtcNow));

        try
        {
            await db.SaveChangesAsync(ct);
            return Result.Success(generated);
        }
        catch (DbUpdateException)
        {
            // The unique criteria key is an idempotency boundary. A concurrent generator wins or
            // loses atomically; the loser always returns the winner's persisted snapshot.
            db.ClearTrackedEntities();
            persisted = await db.StatisticalReportSnapshots.AsNoTracking()
                .SingleOrDefaultAsync(x => x.CriteriaKey == criteriaKey, ct);
            return persisted is null
                ? Result.Failure<StatisticalReportDto>("statistical_report.persistence_failed", "The report snapshot could not be saved.")
                : Deserialize(persisted.PayloadJson);
        }
    }

    private async Task<StatisticalReportDto> BuildAsync(string reportType, string bookingType,
        long? operatorUserId, long? destinationId, StatisticalPeriodDto period, CancellationToken ct) => reportType switch
    {
        "PLATFORM_REVENUE" => await BuildRevenueAsync(bookingType, operatorUserId, destinationId, period, ct),
        "BOOKING_VOLUME" => await BuildBookingVolumeAsync(bookingType, operatorUserId, destinationId, period, ct),
        "USER_GROWTH" => await BuildUserGrowthAsync(period, ct),
        "OPERATOR_PERFORMANCE" => await BuildOperatorPerformanceAsync(operatorUserId, destinationId, period, ct),
        "DESTINATION_POPULARITY" => await BuildDestinationPopularityAsync(operatorUserId, destinationId, period, ct),
        _ => throw new InvalidOperationException("Validated report type is unavailable."),
    };

    private async Task<StatisticalReportDto> BuildRevenueAsync(string bookingType, long? operatorUserId,
        long? destinationId, StatisticalPeriodDto period, CancellationToken ct)
    {
        FinancialDataset data = await LoadFinancialDatasetAsync(bookingType, operatorUserId, destinationId, period, ct);
        decimal tourCommission = data.Tours.Sum(x => x.Commission);
        decimal serviceCommission = data.Services.Sum(x => x.Commission);
        decimal refundReversal = data.RefundCommissionReversal;
        decimal net = tourCommission + serviceCommission - refundReversal;

        return new StatisticalReportDto("PLATFORM_REVENUE", period, clock.UtcNow,
        [
            new("netPlatformRevenue", "Net Platform Revenue", net, true),
            new("tourCommission", "Tour Commission Recognized", tourCommission, true),
            new("serviceCommission", "Service Commission Recognized", serviceCommission, true),
            new("refundCommissionReversal", "Less Processed Refund Commission Reversal", refundReversal, true),
        ], new(tourCommission, serviceCommission, refundReversal, net),
        [
            new("tour", "Tour package commissions", tourCommission, data.Tours.Sum(x => x.GrossAmount), data.Tours.Count, true),
            new("service", "Commercial service commissions", serviceCommission, data.Services.Sum(x => x.GrossAmount), data.Services.Count, true),
            new("refund", "Less processed refund commission", refundReversal, data.RefundAmount, data.RefundCount, true),
        ]);
    }

    private async Task<StatisticalReportDto> BuildBookingVolumeAsync(string bookingType, long? operatorUserId,
        long? destinationId, StatisticalPeriodDto period, CancellationToken ct)
    {
        FinancialDataset data = await LoadFinancialDatasetAsync(bookingType, operatorUserId, destinationId, period, ct);
        int tourCount = data.Tours.Count;
        int serviceCount = data.Services.Count;
        int total = tourCount + serviceCount;
        return new StatisticalReportDto("BOOKING_VOLUME", period, clock.UtcNow,
        [
            new("totalBookings", "Eligible confirmed or completed bookings", total, false),
            new("tourBookings", "Tour package bookings", tourCount, false),
            new("serviceBookings", "Commercial service bookings", serviceCount, false),
            new("refundedBookings", "Processed refunds", data.RefundCount, false),
        ], null,
        [
            new("tour", "Tour package bookings", tourCount, data.Tours.Sum(x => x.GrossAmount), data.Tours.Sum(x => x.Quantity), false),
            new("service", "Commercial service bookings", serviceCount, data.Services.Sum(x => x.GrossAmount), data.Services.Sum(x => x.Quantity), false),
            new("refund", "Processed traveler refunds", data.RefundCount, data.RefundAmount, 0m, false),
        ]);
    }

    private async Task<StatisticalReportDto> BuildUserGrowthAsync(StatisticalPeriodDto period, CancellationToken ct)
    {
        DateTimeOffset periodStart = ToUtcStart(period.StartDate);
        DateTimeOffset until = ToUtcStart(period.EndDate.AddDays(1));
        var counts = await db.Users.AsNoTracking().Where(x => x.CreatedAtUtc >= periodStart && x.CreatedAtUtc < until)
            .GroupBy(x => x.Role).Select(x => new { Role = x.Key, Count = x.Count() }).ToListAsync(ct);
        int travelers = counts.Where(x => x.Role == UserRole.Traveler).Sum(x => x.Count);
        int operators = counts.Where(x => x.Role == UserRole.TourOperator).Sum(x => x.Count);
        int staff = counts.Where(x => x.Role is UserRole.Staff or UserRole.Administrator).Sum(x => x.Count);
        return new StatisticalReportDto("USER_GROWTH", period, clock.UtcNow,
        [new("totalNewAccounts", "Total new accounts", travelers + operators + staff, false), new("newTravelers", "New traveler accounts", travelers, false), new("newOperators", "New tour operator accounts", operators, false)], null,
        [new("traveler", "Travelers", travelers, 0m, 0m, false), new("operator", "Tour operators", operators, 0m, 0m, false), new("staff", "Staff and administrators", staff, 0m, 0m, false)]);
    }

    private async Task<StatisticalReportDto> BuildOperatorPerformanceAsync(long? operatorUserId, long? destinationId,
        StatisticalPeriodDto period, CancellationToken ct)
    {
        FinancialDataset data = await LoadFinancialDatasetAsync("TOUR_PACKAGE", operatorUserId, destinationId, period, ct);
        var rows = data.Tours.GroupBy(x => new { x.OperatorUserId, x.OperatorName }).OrderByDescending(x => x.Sum(y => y.Commission))
            .Select(x => new StatisticalBreakdownDto(x.Key.OperatorUserId.ToString(), x.Key.OperatorName,
                x.Sum(y => y.Commission), x.Sum(y => y.GrossAmount), x.Count(), true)).ToList();
        return new StatisticalReportDto("OPERATOR_PERFORMANCE", period, clock.UtcNow,
        [new("operators", "Operators with eligible tour bookings", rows.Count, false), new("recognizedCommission", "Tour commission recognized", data.Tours.Sum(x => x.Commission), true)], null, rows);
    }

    private async Task<StatisticalReportDto> BuildDestinationPopularityAsync(long? operatorUserId, long? destinationId,
        StatisticalPeriodDto period, CancellationToken ct)
    {
        FinancialDataset data = await LoadFinancialDatasetAsync("TOUR_PACKAGE", operatorUserId, destinationId, period, ct);
        var destinations = await db.TourDestinations.AsNoTracking().Where(x => data.Tours.Select(y => y.TourId).Contains(x.TourId))
            .Select(x => new { x.TourId, x.DestinationId, x.Destination.Name }).ToListAsync(ct);
        var byTour = data.Tours.GroupBy(x => x.TourId).ToDictionary(x => x.Key, x => x.ToList());
        var rows = destinations.GroupBy(x => new { x.DestinationId, x.Name }).Select(group =>
        {
            var bookings = group.SelectMany(link => byTour.GetValueOrDefault(link.TourId, [])).ToList();
            return new StatisticalBreakdownDto(group.Key.DestinationId.ToString(), group.Key.Name,
                bookings.Count, bookings.Sum(x => x.GrossAmount), bookings.Sum(x => x.Quantity), false);
        }).OrderByDescending(x => x.PrimaryValue).ToList();
        return new StatisticalReportDto("DESTINATION_POPULARITY", period, clock.UtcNow,
        [new("destinations", "Destinations with booking activity", rows.Count, false), new("tourBookings", "Eligible tour bookings", data.Tours.Count, false)], null, rows);
    }

    private async Task<FinancialDataset> LoadFinancialDatasetAsync(string bookingType, long? operatorUserId,
        long? destinationId, StatisticalPeriodDto period, CancellationToken ct)
    {
        DateTimeOffset periodStart = ToUtcStart(period.StartDate);
        DateTimeOffset until = ToUtcStart(period.EndDate.AddDays(1));
        var tourRows = bookingType == "COMMERCIAL_SERVICE" ? [] : await db.PaymentTransactions.AsNoTracking()
            .Where(p => p.TransactionType == PaymentTransaction.TypePayment && p.Status == PaymentTransactionStatus.Success &&
                (p.Booking.Status == BookingStatus.Confirmed || p.Booking.Status == BookingStatus.Completed) &&
                p.Booking.PaymentStatus == PaymentStatus.Paid && (p.SucceededAtUtc ?? p.CreatedAtUtc) >= periodStart &&
                (p.SucceededAtUtc ?? p.CreatedAtUtc) < until && (!operatorUserId.HasValue || p.Booking.TourSchedule.Tour.OperatorUserId == operatorUserId.Value))
            .Select(p => new TourRow(p.BookingId, p.Booking.TourSchedule.TourId, p.Booking.TourSchedule.Tour.OperatorUserId,
                p.Booking.TourSchedule.Tour.OperatorProfile.CompanyName, p.Amount, p.Booking.Quantity,
                Math.Round(p.Amount * (p.CommissionRateApplied ?? 0m) / 100m, 2))).ToListAsync(ct);

        HashSet<long>? destinationTourIds = null;
        if (destinationId.HasValue)
        {
            var eligibleTourIds = await db.TourDestinations.AsNoTracking().Where(x => x.DestinationId == destinationId.Value)
                .Select(x => x.TourId).ToListAsync(ct);
            destinationTourIds = eligibleTourIds.ToHashSet();
            tourRows = tourRows.Where(x => destinationTourIds.Contains(x.TourId)).ToList();
        }

        var serviceRows = bookingType == "TOUR_PACKAGE" || operatorUserId.HasValue || destinationId.HasValue ? [] : await db.ServiceBookings.AsNoTracking()
            .Where(x => (x.Status == "Confirmed" || x.Status == "Completed") && x.CreatedAtUtc >= periodStart && x.CreatedAtUtc < until)
            .Select(x => new ServiceRow(x.Id, x.TotalPrice, x.Quantity, x.CommissionAmount ?? 0m)).ToListAsync(ct);

        List<RefundRow> refundRows;
        if (bookingType == "COMMERCIAL_SERVICE")
        {
            refundRows = [];
        }
        else
        {
            var refundQuery = from refund in db.Refunds.AsNoTracking()
                              join payment in db.PaymentTransactions.AsNoTracking() on refund.TransactionId equals payment.Id
                              where refund.Status == RefundStatus.Processed && refund.ProcessedAtUtc != null && refund.ProcessedAtUtc >= periodStart && refund.ProcessedAtUtc < until &&
                                    payment.TransactionType == PaymentTransaction.TypePayment && payment.Status == PaymentTransactionStatus.Success &&
                                    (!operatorUserId.HasValue || payment.Booking.TourSchedule.Tour.OperatorUserId == operatorUserId.Value)
                              select new RefundRow(refund.BookingId, refund.Amount, Math.Round(refund.Amount * (payment.CommissionRateApplied ?? 0m) / 100m, 2), payment.Booking.TourSchedule.TourId);
            refundRows = await refundQuery.ToListAsync(ct);
        }

        if (destinationId.HasValue && refundRows.Count > 0)
        {
            // A refund may be processed in this period for a payment that succeeded in an earlier
            // period, so it must be filtered against the destination relation itself rather than
            // the current period's payment rows.
            refundRows = refundRows.Where(x => destinationTourIds!.Contains(x.TourId)).ToList();
        }

        return new FinancialDataset(tourRows, serviceRows, refundRows.Sum(x => x.CommissionReversal), refundRows.Sum(x => x.Amount), refundRows.Count);
    }

    private static string? ValidateScope(string reportType, string bookingType, long? operatorId, long? destinationId)
    {
        if (reportType == "USER_GROWTH" && (operatorId.HasValue || destinationId.HasValue || bookingType != "ALL"))
            return "User Growth is platform-wide and cannot be filtered by operator, destination, or booking type.";
        if (reportType is "OPERATOR_PERFORMANCE" or "DESTINATION_POPULARITY" && bookingType == "COMMERCIAL_SERVICE")
            return "This report type is available for tour package data only.";
        if (bookingType == "COMMERCIAL_SERVICE" && (operatorId.HasValue || destinationId.HasValue))
            return "Commercial service bookings do not have a Tour Operator or Destination reporting dimension.";
        return null;
    }

    private static Result<StatisticalReportDto> Deserialize(string payload) => JsonSerializer.Deserialize<StatisticalReportDto>(payload, JsonOptions) is { } report
        ? Result.Success(report)
        : Result.Failure<StatisticalReportDto>("statistical_report.snapshot_invalid", "The persisted report snapshot is invalid.");

    private static string CreateCriteriaKey(string reportType, string periodKey, string bookingType, long? operatorId, long? destinationId)
    {
        string input = string.Join('|', reportType, periodKey, bookingType, operatorId?.ToString() ?? "ALL", destinationId?.ToString() ?? "ALL");
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(input))).ToLowerInvariant();
    }

    private static StatisticalPeriodDto ToDto(ClosedStatisticalPeriod period) => new(period.PeriodKey, period.StartDate, period.EndDate, period.ClosedAtUtc);
    private static DateTimeOffset ToUtcStart(DateOnly value) => new DateTimeOffset(value.ToDateTime(TimeOnly.MinValue), TimeSpan.FromHours(7)).ToUniversalTime();
    private sealed record TourRow(long BookingId, long TourId, long OperatorUserId, string OperatorName, decimal GrossAmount, int Quantity, decimal Commission);
    private sealed record ServiceRow(long BookingId, decimal GrossAmount, int Quantity, decimal Commission);
    private sealed record RefundRow(long BookingId, decimal Amount, decimal CommissionReversal, long TourId);
    private sealed record FinancialDataset(List<TourRow> Tours, List<ServiceRow> Services, decimal RefundCommissionReversal, decimal RefundAmount, int RefundCount);
}
