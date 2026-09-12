using MediatR;

using Microsoft.EntityFrameworkCore;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Common.Models;
using TripMate.Domain.Enums;

namespace TripMate.Application.Features.PointsOfInterest.Explore;

public sealed class ExplorePoisQueryHandler(IApplicationDbContext dbContext)
    : IRequestHandler<ExplorePoisQuery, Result<PagedPoiResponseDto>>
{
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
            var trimmedSearch = request.Search.Trim();
            var searchPattern = $"%{trimmedSearch}%";
            query = query.Where(p => EF.Functions.Like(p.Name, searchPattern));
        }

        var totalCount = await query.CountAsync(cancellationToken);

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

        query = query
            .OrderBy(p => p.Name)
            .ThenBy(p => p.Id);

        var items = await query
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(p => new PoiListItemDto(
                p.Id,
                p.Name,
                p.CategoryId,
                p.Category.Name,
                p.Latitude,
                p.Longitude,
                p.Address,
                p.IndoorOutdoor,
                p.AverageVisitDurationMinutes,
                p.HasShelter,
                null,
                0,
                null,
                null,
                false))
            .ToListAsync(cancellationToken);

        return Result.Success(new PagedPoiResponseDto(
            page,
            pageSize,
            totalCount,
            totalPages,
            items));
    }
}