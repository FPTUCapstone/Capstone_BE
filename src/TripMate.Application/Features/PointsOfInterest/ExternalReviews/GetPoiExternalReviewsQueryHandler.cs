using MediatR;

using Microsoft.EntityFrameworkCore;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Common.Models;
using TripMate.Domain.Enums;

namespace TripMate.Application.Features.PointsOfInterest.ExternalReviews;

public sealed class GetPoiExternalReviewsQueryHandler(
    IApplicationDbContext dbContext,
    ITripadvisorClient tripadvisor,
    IReviewTranslator translator)
    : IRequestHandler<GetPoiExternalReviewsQuery, Result<PoiExternalReviewsDto>>
{
    public const string TripadvisorSource = "Tripadvisor";

    public async Task<Result<PoiExternalReviewsDto>> Handle(
        GetPoiExternalReviewsQuery request,
        CancellationToken cancellationToken)
    {
        var poi = await dbContext.PointsOfInterest
            .AsNoTracking()
            .Where(item => item.Id == request.PoiId && item.Status == PointOfInterestStatus.Active)
            .Select(item => new { item.Id, item.TripadvisorLocationId })
            .SingleOrDefaultAsync(cancellationToken);
        if (poi is null)
        {
            return Result.Failure<PoiExternalReviewsDto>(
                ExternalReviewErrorCodes.PoiNotFound,
                "The location was not found.");
        }

        if (poi.TripadvisorLocationId is not { } locationId || !tripadvisor.IsEnabled)
        {
            return Result.Success(Empty(poi.Id));
        }

        var ratings = await tripadvisor.GetRatingsAsync([locationId], cancellationToken);
        var reviews = await tripadvisor.GetReviewsAsync(
            locationId,
            Math.Clamp(request.Size, 1, GetPoiExternalReviewsQuery.MaxSize),
            cancellationToken);
        if (!ratings.TryGetValue(locationId, out var rating) && reviews.Count == 0)
        {
            return Result.Success(Empty(poi.Id));
        }

        return Result.Success(new PoiExternalReviewsDto(
            poi.Id,
            TripadvisorSource,
            rating?.Rating,
            rating?.ReviewCount ?? 0,
            rating?.RatingImageUrl,
            rating?.Url,
            reviews,
            translator.IsEnabled));
    }

    private static PoiExternalReviewsDto Empty(long poiId) =>
        new(poiId, null, null, 0, null, null, [], false);
}