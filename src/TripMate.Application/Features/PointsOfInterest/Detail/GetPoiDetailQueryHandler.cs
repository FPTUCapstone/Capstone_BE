using MediatR;

using Microsoft.EntityFrameworkCore;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Common.Models;
using TripMate.Application.Features.PointsOfInterest.Common;
using TripMate.Domain.Enums;

namespace TripMate.Application.Features.PointsOfInterest.Detail;

public sealed class GetPoiDetailQueryHandler(
    IApplicationDbContext dbContext,
    IDateTimeProvider dateTimeProvider)
    : IRequestHandler<GetPoiDetailQuery, Result<PoiDetailDto>>
{
    public async Task<Result<PoiDetailDto>> Handle(
        GetPoiDetailQuery request,
        CancellationToken cancellationToken)
    {
        var poi = await dbContext.PointsOfInterest
            .AsNoTracking()
            .Include(p => p.Category)
            .Include(p => p.OpeningHours)
            .Include(p => p.PoiTags)
                .ThenInclude(pt => pt.Tag)
            .FirstOrDefaultAsync(
                p => p.Id == request.Id && p.Status == PointOfInterestStatus.Active,
                cancellationToken);

        if (poi is null)
        {
            return Result.Failure<PoiDetailDto>(
                PoiErrorCodes.NotFound,
                PoiErrorMessages.NotFound);
        }

        var photos = await dbContext.PoiPhotos
            .AsNoTracking()
            .Where(ph => ph.PointOfInterestId == request.Id)
            .OrderBy(ph => ph.SortOrder)
            .ThenBy(ph => ph.Id)
            .Select(ph => new PoiPhotoDto(ph.Id, ph.Url, ph.Caption, ph.SortOrder))
            .ToListAsync(cancellationToken);

        var reviewStats = await dbContext.Reviews
            .AsNoTracking()
            .Where(r => r.TargetType == "POI" && r.TargetId == request.Id)
            .GroupBy(_ => 1)
            .Select(g => new
            {
                Average = g.Average(r => (decimal?)r.Rating),
                Count = g.Count(),
            })
            .FirstOrDefaultAsync(cancellationToken);

        var averageRating = reviewStats?.Average.HasValue == true
            ? Math.Round(reviewStats.Average.Value, 1, MidpointRounding.AwayFromZero)
            : (decimal?)null;
        var reviewCount = reviewStats?.Count ?? 0;

        var isOpenNow = PoiOpeningState.IsOpenNow(poi.OpeningHours, dateTimeProvider.UtcNow);

        var openingHoursDto = poi.OpeningHours
            .OrderBy(h => h.DayOfWeek)
            .Select(h => new PoiOpeningHourDto(
                h.DayOfWeek,
                h.OpenTime,
                h.CloseTime,
                h.IsClosed))
            .ToList();

        var tagsDto = poi.PoiTags
            .Select(pt => pt.Tag)
            .Where(t => t != null)
            .OrderBy(t => t.Name)
            .ThenBy(t => t.Id)
            .Select(t => new PoiTagDto(t.Id, t.Name))
            .ToList();

        var detailDto = new PoiDetailDto(
            poi.Id,
            poi.Name,
            poi.Description,
            poi.Status,
            poi.CategoryId,
            poi.Category.Name,
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
            openingHoursDto,
            photos,
            tagsDto,
            poi.CreatedAtUtc,
            poi.UpdatedAtUtc);

        return Result.Success(detailDto);
    }
}