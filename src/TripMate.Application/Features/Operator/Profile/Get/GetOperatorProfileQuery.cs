using MediatR;

using TripMate.Application.Common.Models;
using TripMate.Application.Features.Operator.Profile.Common;

namespace TripMate.Application.Features.Operator.Profile.Get;

public sealed record GetOperatorProfileQuery : IRequest<Result<OperatorProfileDto>>;