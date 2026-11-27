using MediatR;

using Microsoft.EntityFrameworkCore;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Common.Models;
using TripMate.Application.Features.PointsOfInterest.Common;
using TripMate.Application.Features.TripReviews.Common;
using TripMate.Domain.Enums;

namespace TripMate.Application.Features.RecommendationFeedback.Favorites;

public sealed class ListFavoritePoisQueryHandler(
    IApplicationDbContext dbContext,
    IDateTimeProvider dateTimeProvider)
    : IRequestHandler<ListFavoritePoisQuery, Result<PagedFavoritePoisResponse>>
{
    public async Task<Result<PagedFavoritePoisResponse>> Handle(
        ListFavoritePoisQuery query,
        CancellationToken cancellationToken)
    {
        var page = query.Page >= ListFavoritePoisQuery.MinPage
            ? query.Page
            : ListFavoritePoisQuery.DefaultPage;
        var pageSize = query.PageSize >= ListFavoritePoisQuery.MinPageSize
            ? query.PageSize
            : ListFavoritePoisQuery.DefaultPageSize;

        var activeFavorites =
            from favorite in dbContext.PoiFavorites.AsNoTracking()
            join poi in dbContext.PointsOfInterest.AsNoTracking()
                on favorite.PointOfInterestId equals poi.Id
            where favorite.TravelerUserId == query.TravelerUserId
                && poi.Status == PointOfInterestStatus.Active
            select new
            {
                Favorite = favorite,
                Poi = poi,
            };

        var totalCount = await activeFavorites.CountAsync(cancellationToken);
        if (totalCount == 0)
        {
            return Result.Success(new PagedFavoritePoisResponse(page, pageSize, 0, 0, []));
        }

        var totalPages = (int)Math.Ceiling(totalCount / (double)pageSize);
        if (page > totalPages)
        {
            return Result.Success(new PagedFavoritePoisResponse(page, pageSize, totalCount, totalPages, []));
        }

        var (vietnamDay, vietnamTime) = PoiOpeningState.GetVietnamDayAndTime(dateTimeProvider.UtcNow);
        var offset = checked((page - 1) * pageSize);

        var pageRecords = await activeFavorites
            .OrderByDescending(x => x.Favorite.CreatedAtUtc)
            .ThenByDescending(x => x.Poi.Id)
            .Skip(offset)
            .Take(pageSize)
            .Select(x => new
            {
                x.Poi.Id,
                x.Poi.Name,
                x.Poi.CategoryId,
                CategoryName = x.Poi.Category.Name,
                x.Poi.Latitude,
                x.Poi.Longitude,
                x.Poi.Address,
                x.Poi.IndoorOutdoor,
                x.Poi.AverageVisitDurationMinutes,
                x.Poi.HasShelter,
                AverageRating = TripReviewAggregateReader.QueryPoiReviews(dbContext)
                    .Where(r => r.TargetId == x.Poi.Id)
                    .Average(r => (decimal?)r.Rating),
                ReviewCount = TripReviewAggregateReader.QueryPoiReviews(dbContext)
                    .Count(r => r.TargetId == x.Poi.Id),
                ThumbnailUrl = dbContext.PoiPhotos
                    .Where(ph => ph.PointOfInterestId == x.Poi.Id)
                    .OrderBy(ph => ph.SortOrder)
                    .ThenBy(ph => ph.Id)
                    .Select(ph => ph.Url)
                    .FirstOrDefault(),
                IsOpenNow = x.Poi.OpeningHours.Any(h =>
                    h.DayOfWeek == vietnamDay
                    && !h.IsClosed
                    && h.OpenTime <= vietnamTime
                    && vietnamTime < h.CloseTime),
                FavoritedAtUtc = x.Favorite.CreatedAtUtc,
            })
            .ToListAsync(cancellationToken);

        var items = pageRecords
            .Select(x => new FavoritePoiItemDto(
                x.Id,
                x.Name,
                x.CategoryId,
                x.CategoryName,
                x.Latitude,
                x.Longitude,
                x.Address,
                x.IndoorOutdoor,
                x.AverageVisitDurationMinutes,
                x.HasShelter,
                TripReviewAggregateDto.Round(x.AverageRating),
                x.ReviewCount,
                x.ThumbnailUrl,
                x.IsOpenNow,
                x.FavoritedAtUtc))
            .ToList();

        return Result.Success(new PagedFavoritePoisResponse(
            page,
            pageSize,
            totalCount,
            totalPages,
            items));
    }
}