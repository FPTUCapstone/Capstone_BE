using MediatR;

using TripMate.Application.Common.Models;
using TripMate.Application.Features.TourRecommendations.Common;

namespace TripMate.Application.Features.TourRecommendations.GetLatest;

public sealed record GetLatestTourRecommendationsQuery(int Page = 1)
    : IRequest<Result<TourRecommendationPageDto>>;