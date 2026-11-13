using MediatR;

using TripMate.Application.Common.Models;
using TripMate.Application.Features.Admin.StatisticalReports.Common;

namespace TripMate.Application.Features.Admin.StatisticalReports.GetOperators;

public sealed record GetReportingTourOperatorsQuery() : IRequest<Result<IReadOnlyList<StatisticalReportOperatorDto>>>;