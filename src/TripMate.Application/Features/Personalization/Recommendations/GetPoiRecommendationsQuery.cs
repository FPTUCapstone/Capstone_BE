using MediatR;

using TripMate.Application.Common.Models;

namespace TripMate.Application.Features.Personalization.Recommendations;

public sealed record GetPoiRecommendationsQuery(
    long TravelerUserId,
    decimal ExplorationLatitude,
    decimal ExplorationLongitude,
    int SearchRadiusKm,
    int? Limit)
    : IRequest<Result<PoiRecommendationResultDto>>;