using MediatR;
using TripMate.Application.Common.Models;
using TripMate.Application.Features.Admin.StatisticalReports.Common;

namespace TripMate.Application.Features.Admin.StatisticalReports.GetPeriods;

public sealed record GetClosedStatisticalPeriodsQuery() : IRequest<Result<IReadOnlyList<StatisticalPeriodDto>>>;
