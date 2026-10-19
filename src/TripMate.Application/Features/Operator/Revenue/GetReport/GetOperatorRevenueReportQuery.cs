using MediatR;
using TripMate.Application.Common.Models;

namespace TripMate.Application.Features.Operator.Revenue.GetReport;

public sealed record GetOperatorRevenueReportQuery(
    DateOnly StartDate,
    DateOnly EndDate,
    string Granularity = "monthly",
    long? TourId = null,
    int Page = 1,
    int PageSize = 20) : IRequest<Result<OperatorRevenueReportResponse>>;

public sealed record OperatorRevenueSummaryDto(decimal GrossRevenue, decimal PaymentsReceived, decimal DiscountsGiven, decimal RefundedAmount, decimal NetSales, decimal PlatformCommission, decimal NetAmount, int TotalBookings, int TotalParticipants, decimal CommissionRatePercent);
public sealed record OperatorRevenueChartDto(string PeriodKey, string PeriodLabel, decimal GrossRevenue, decimal Commission, decimal NetAmount, decimal RefundedAmount, int BookingsCount);
public sealed record OperatorTourRevenueDto(long TourId, string TourCode, string TourName, int BookingsCount, int ParticipantsCount, decimal GrossRevenue, decimal RefundedAmount, decimal Commission, decimal NetAmount);
public sealed record OperatorRevenueReportResponse(OperatorRevenueSummaryDto Summary, IReadOnlyList<OperatorRevenueChartDto> ChartData, IReadOnlyList<OperatorTourRevenueDto> TourDetails, int TotalToursCount, int Page, int PageSize, int TotalPages);
