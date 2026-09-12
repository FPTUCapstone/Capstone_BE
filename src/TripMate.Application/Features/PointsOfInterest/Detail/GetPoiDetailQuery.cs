using MediatR;

using TripMate.Application.Common.Models;

namespace TripMate.Application.Features.PointsOfInterest.Detail;

public sealed record GetPoiDetailQuery(long Id) : IRequest<Result<PoiDetailDto>>;