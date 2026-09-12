using MediatR;

using Microsoft.EntityFrameworkCore;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Common.Models;
using TripMate.Application.Features.PointsOfInterest.Common;
using TripMate.Domain.Enums;

namespace TripMate.Application.Features.PointsOfInterest.Explore;

public sealed class ExplorePoisQueryHandler(
    IApplicationDbContext dbContext,
    IDateTimeProvider dateTimeProvider)
    : IRequestHandler<ExplorePoisQuery, Result<PagedPoiResponseDto>>
{
    private const double EarthRadiusKm = 6371.0088;
    private const double DegreesToRadians = Math.PI / 180.0;

    public async Task<Result<PagedPoiResponseDto>> Handle(
        ExplorePoisQuery request,
        CancellationToken cancellationToken)
    {
        var query = dbContext.PointsOfInterest
            .AsNoTracking()
            .Where(p => p.Status == PointOfInterestStatus.Active);

        if (request.CategoryId.HasValue)
        {
            query = query.Where(p => p.CategoryId == request.CategoryId.Value);
        }

        if (!string.IsNullOrWhiteSpace(request.Search))
        {
            var searchPattern = $"%{request.Search.Trim()}%";
            query = query.Where(p => EF.Functions.Like(p.Name, searchPattern));
        }

        var (vietnamDay, vietnamTime) = PoiOpeningState.GetVietnamDayAndTime(dateTimeProvider.UtcNow);

        if (request.OpenNow)
        {
            query = query.Where(p => p.OpeningHours.Any(h =>
                h.DayOfWeek == vietnamDay
                && !h.IsClosed
                && h.OpenTime <= vietnamTime
                && vietnamTime < h.CloseTime));
        }

        decimal? originLat = request.OriginLatitude.HasValue
            ? Math.Round(request.OriginLatitude.Value, 6, MidpointRounding.AwayFromZero)
            : null;

        decimal? originLon = request.OriginLongitude.HasValue
            ? Math.Round(request.OriginLongitude.Value, 6, MidpointRounding.AwayFromZero)
            : null;

        var hasOrigin = originLat.HasValue && originLon.HasValue;
        var lat1Rad = hasOrigin ? (double)originLat!.Value * DegreesToRadians : 0.0;
        var lon1Rad = hasOrigin ? (double)originLon!.Value * DegreesToRadians : 0.0;

        var projected = query.Select(p => new
        {
            Poi = p,
            Distance = hasOrigin
                ? (double?)(EarthRadiusKm * 2.0 * Math.Asin(Math.Sqrt(
                    Math.Sin(((double)p.Latitude * DegreesToRadians - lat1Rad) / 2.0)
                    * Math.Sin(((double)p.Latitude * DegreesToRadians - lat1Rad) / 2.0)
                    + Math.Cos(lat1Rad) * Math.Cos((double)p.Latitude * DegreesToRadians)
                    * Math.Sin(((double)p.Longitude * DegreesToRadians - lon1Rad) / 2.0)
                    * Math.Sin(((double)p.Longitude * DegreesToRadians - lon1Rad) / 2.0))))
                : null,
            AverageRating = dbContext.Reviews
                .Where(r => r.TargetType == "POI" && r.TargetId == p.Id)
                .Average(r => (decimal?)r.Rating),
            ReviewCount = dbContext.Reviews
                .Where(r => r.TargetType == "POI" && r.TargetId == p.Id)
                .Count(),
            ThumbnailUrl = dbContext.PoiPhotos
                .Where(ph => ph.PointOfInterestId == p.Id)
                .OrderBy(ph => ph.SortOrder)
                .ThenBy(ph => ph.Id)
                .Select(ph => ph.Url)
                .FirstOrDefault(),
            IsOpenNow = p.OpeningHours.Any(h =>
                h.DayOfWeek == vietnamDay
                && !h.IsClosed
                && h.OpenTime <= vietnamTime
                && vietnamTime < h.CloseTime),
        });

        if (hasOrigin && request.MaxDistanceKm.HasValue)
        {
            var maxDistance = (double)request.MaxDistanceKm.Value;
            projected = projected.Where(x => x.Distance <= maxDistance);
        }

        var totalCount = await projected.CountAsync(cancellationToken);

        var page = request.Page > 0 ? request.Page : ExplorePoisQuery.DefaultPage;
        var pageSize = request.PageSize > 0 ? request.PageSize : ExplorePoisQuery.DefaultPageSize;

        if (totalCount == 0)
        {
            return Result.Success(new PagedPoiResponseDto(
                page,
                pageSize,
                0,
                0,
                []));
        }

        var totalPages = (int)Math.Ceiling(totalCount / (double)pageSize);

        var sorted = request.Sort switch
        {
            "distance" => projected
                .OrderBy(x => x.Distance)
                .ThenBy(x => x.Poi.Name)
                .ThenBy(x => x.Poi.Id),
            "rating" => projected
                .OrderByDescending(x => x.AverageRating.HasValue)
                .ThenByDescending(x => x.AverageRating)
                .ThenByDescending(x => x.ReviewCount)
                .ThenBy(x => x.Poi.Name)
                .ThenBy(x => x.Poi.Id),
            _ => projected
                .OrderBy(x => x.Poi.Name)
                .ThenBy(x => x.Poi.Id),
        };

        var pageRecords = await sorted
            .Skip((page - 1) * pageSize)
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
                x.AverageRating,
                x.ReviewCount,
                x.ThumbnailUrl,
                x.Distance,
                x.IsOpenNow,
            })
            .ToListAsync(cancellationToken);

        var items = pageRecords.Select(x => new PoiListItemDto(
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
            x.AverageRating.HasValue
                ? Math.Round(x.AverageRating.Value, 1, MidpointRounding.AwayFromZero)
                : null,
            x.ReviewCount,
            x.ThumbnailUrl,
            x.Distance.HasValue
                ? Math.Round((decimal)x.Distance.Value, 2, MidpointRounding.AwayFromZero)
                : null,
            x.IsOpenNow))
            .ToList();

        return Result.Success(new PagedPoiResponseDto(
            page,
            pageSize,
            totalCount,
            totalPages,
            items));
    }
}