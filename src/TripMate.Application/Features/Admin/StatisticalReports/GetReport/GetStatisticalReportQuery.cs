using MediatR;
using TripMate.Application.Common.Models;
using TripMate.Application.Features.Admin.StatisticalReports.Common;

namespace TripMate.Application.Features.Admin.StatisticalReports.GetReport;

public sealed record GetStatisticalReportQuery(
    string ReportType,
    string PeriodKey,
    string BookingType = "ALL",
    long? OperatorUserId = null,
    long? DestinationId = null) : IRequest<Result<StatisticalReportDto>>;
