using MediatR;

using TripMate.Application.Common.Models;

namespace TripMate.Application.Features.RecommendationFeedback.Favorites;

public sealed record GetPoiFavoriteQuery(long TravelerUserId, long PoiId)
    : IRequest<Result<PoiFavoriteResponse>>;