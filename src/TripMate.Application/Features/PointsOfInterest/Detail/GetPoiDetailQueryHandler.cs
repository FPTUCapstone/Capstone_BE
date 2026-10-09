using MediatR;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Common.Models;
using TripMate.Application.Features.PointsOfInterest.Common;
using TripMate.Application.Features.TripReviews.Common;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Application.Features.PointsOfInterest.Detail;

public sealed class GetPoiDetailQueryHandler(
    IApplicationDbContext dbContext,
    IDateTimeProvider dateTimeProvider,
    ILogger<GetPoiDetailQueryHandler> logger)
    : IRequestHandler<GetPoiDetailQuery, Result<PoiDetailDto>>
{
    public async Task<Result<PoiDetailDto>> Handle(
        GetPoiDetailQuery request,
        CancellationToken cancellationToken)
    {
        var poi = await dbContext.PointsOfInterest
            .AsNoTracking()
            .Where(p => p.Id == request.Id && p.Status == PointOfInterestStatus.Active)
            .Select(p => new
            {
                p.Id,
                p.Name,
                p.Description,
                p.Status,
                p.CategoryId,
                CategoryName = p.Category.Name,
                p.Latitude,
                p.Longitude,
                p.Address,
                p.IndoorOutdoor,
                p.AverageVisitDurationMinutes,
                p.HasShelter,
                p.ScenicScore,
                p.PhotoRating,
                p.CreatedAtUtc,
                p.UpdatedAtUtc,
            })
            .FirstOrDefaultAsync(cancellationToken);

        if (poi is null)
        {
            return Result.Failure<PoiDetailDto>(
                PoiErrorCodes.NotFound,
                PoiErrorMessages.NotFound);
        }

        var openingHours = await dbContext.PoiOpeningHours
            .AsNoTracking()
            .Where(h => h.PointOfInterestId == request.Id)
            .OrderBy(h => h.DayOfWeek)
            .Select(h => new PoiOpeningHourDto(
                h.DayOfWeek,
                h.OpenTime,
                h.CloseTime,
                h.IsClosed))
            .ToListAsync(cancellationToken);

        var photos = await dbContext.PoiPhotos
            .AsNoTracking()
            .Where(ph => ph.PointOfInterestId == request.Id)
            .OrderBy(ph => ph.SortOrder)
            .ThenBy(ph => ph.Id)
            .Select(ph => new PoiPhotoDto(ph.Id, ph.Url, ph.Caption, ph.SortOrder))
            .ToListAsync(cancellationToken);

        var tags = await dbContext.PoiTags
            .AsNoTracking()
            .Where(pt => pt.PointOfInterestId == request.Id)
            .OrderBy(pt => pt.Tag.Name)
            .ThenBy(pt => pt.TagId)
            .Select(pt => new PoiTagDto(pt.TagId, pt.Tag.Name))
            .ToListAsync(cancellationToken);

        var reviewStats = await dbContext.PointsOfInterest
            .Where(point => point.Id == request.Id)
            .Select(_ => new
            {
                AverageRating = TripReviewAggregateReader.QueryPoiReviews(dbContext)
                    .Where(item => item.TargetId == request.Id)
                    .Average(item => (decimal?)item.Rating),
                ReviewCount = TripReviewAggregateReader.QueryPoiReviews(dbContext)
                    .Count(item => item.TargetId == request.Id),
                HasLegacyConflict = TripReviewAggregateReader
                    .QueryConflictingLegacyPoiTargetIds(dbContext)
                    .Any(targetId => targetId == request.Id),
            })
            .SingleAsync(cancellationToken);

        if (reviewStats.HasLegacyConflict)
        {
            logger.LogWarning(
                "TM-79 aggregate integrity overlap: canonical review won over legacy POI review for POI {PoiId}.",
                request.Id);
        }

        var averageRating = TripReviewAggregateDto.Round(reviewStats.AverageRating);
        var reviewCount = reviewStats.ReviewCount;

        var isOpenNow = PoiOpeningState.IsOpenNow(openingHours, dateTimeProvider.UtcNow);

        var detailDto = new PoiDetailDto(
            poi.Id,
            poi.Name,
            poi.Description,
            poi.Status,
            poi.CategoryId,
            poi.CategoryName,
            poi.Latitude,
            poi.Longitude,
            poi.Address,
            poi.IndoorOutdoor,
            poi.AverageVisitDurationMinutes,
            poi.HasShelter,
            poi.ScenicScore,
            poi.PhotoRating,
            averageRating,
            reviewCount,
            isOpenNow,
            openingHours,
            photos,
            tags,
            poi.CreatedAtUtc,
            poi.UpdatedAtUtc);

        return Result.Success(detailDto);
    }
}