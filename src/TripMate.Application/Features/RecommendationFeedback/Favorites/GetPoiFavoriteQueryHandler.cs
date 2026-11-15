using MediatR;

using Microsoft.EntityFrameworkCore;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Common.Models;

namespace TripMate.Application.Features.RecommendationFeedback.Favorites;

public sealed class GetPoiFavoriteQueryHandler(IApplicationDbContext dbContext)
    : IRequestHandler<GetPoiFavoriteQuery, Result<PoiFavoriteResponse>>
{
    public async Task<Result<PoiFavoriteResponse>> Handle(
        GetPoiFavoriteQuery query,
        CancellationToken cancellationToken)
    {
        bool isFavorite = await dbContext.PoiFavorites
            .AsNoTracking()
            .AnyAsync(
                favorite => favorite.TravelerUserId == query.TravelerUserId
                    && favorite.PointOfInterestId == query.PoiId,
                cancellationToken);
        return Result.Success(new PoiFavoriteResponse(query.PoiId, isFavorite));
    }
}