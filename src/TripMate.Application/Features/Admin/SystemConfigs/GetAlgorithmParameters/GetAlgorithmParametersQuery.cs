using MediatR;

using TripMate.Application.Common.Models;

namespace TripMate.Application.Features.Admin.SystemConfigs.GetAlgorithmParameters;

public record GetAlgorithmParametersQuery()
    : IRequest<Result<AlgorithmParametersDto>>;