using MediatR;

using TripMate.Application.Common.Models;

namespace TripMate.Application.Features.CommercialServices.Detail;

public sealed record GetCommercialServiceDetailQuery(long Id)
    : IRequest<Result<CommercialServiceDetailDto>>;