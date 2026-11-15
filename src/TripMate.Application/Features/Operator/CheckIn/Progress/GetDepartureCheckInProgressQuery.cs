using MediatR;

using TripMate.Application.Common.Models;
using TripMate.Application.Features.Operator.CheckIn.Common;

namespace TripMate.Application.Features.Operator.CheckIn.Progress;

public sealed record GetDepartureCheckInProgressQuery(long ScheduleId)
    : IRequest<Result<DepartureProgressDto>>;