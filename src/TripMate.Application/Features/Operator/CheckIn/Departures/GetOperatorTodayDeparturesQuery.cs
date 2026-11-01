using MediatR;

using TripMate.Application.Common.Models;
using TripMate.Application.Features.Operator.CheckIn.Common;

namespace TripMate.Application.Features.Operator.CheckIn.Departures;

public sealed record GetOperatorTodayDeparturesQuery(DateOnly? TargetDate = null)
    : IRequest<Result<IReadOnlyList<OperatorDepartureSummaryDto>>>;