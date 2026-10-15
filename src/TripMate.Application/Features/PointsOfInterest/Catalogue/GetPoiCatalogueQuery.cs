using MediatR;

using TripMate.Application.Common.Models;

namespace TripMate.Application.Features.PointsOfInterest.Catalogue;

public sealed record GetPoiCatalogueQuery : IRequest<Result<PoiCatalogueDto>>;
