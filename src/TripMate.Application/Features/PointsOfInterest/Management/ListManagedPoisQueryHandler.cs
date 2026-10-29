using System.Globalization;

using MediatR;

using Microsoft.EntityFrameworkCore;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Common.Models;
using TripMate.Application.Features.PointsOfInterest.Common;
using TripMate.Domain.Enums;

namespace TripMate.Application.Features.PointsOfInterest.Management;

public sealed class ListManagedPoisQueryHandler(
    IApplicationDbContext dbContext,
    ICurrentUserService currentUserService)
    : IRequestHandler<ListManagedPoisQuery, Result<ManagedPoiListDto>>
{
    public Task<Result<ManagedPoiListDto>> Handle(ListManagedPoisQuery request, CancellationToken cancellationToken)
    {
        var actorId = currentUserService.UserId;
        if (!actorId.HasValue)
        {
            return Task.FromResult(AccessRequired());
        }

        // The same short read transaction covers current account, catalogue, count and page.
        return dbContext.ExecuteInSerializableTransactionAsync(
            token => ReadListAsync(request, actorId.Value, token), cancellationToken);
    }

    private async Task<Result<ManagedPoiListDto>> ReadListAsync(
        ListManagedPoisQuery request, long actorId, CancellationToken cancellationToken)
    {
        var canRead = await dbContext.Users.AsNoTracking().AnyAsync(
            actor => actor.Id == actorId
                && actor.Status == AccountStatus.Active
                && (actor.Role == UserRole.Administrator || actor.Role == UserRole.Staff),
            cancellationToken);
        if (!canRead)
        {
            return AccessRequired();
        }

        var categories = await dbContext.PoiCategories.AsNoTracking()
            .OrderBy(category => category.Name).ThenBy(category => category.Id)
            .Select(category => new ManagedPoiCategoryDto(category.Id, category.Name))
            .ToListAsync(cancellationToken);
        if (request.CategoryId.HasValue && !categories.Any(category => category.Id == request.CategoryId.Value))
        {
            return Result.Failure<ManagedPoiListDto>(
                ManagedPoiErrors.CategoryNotFound, "The selected POI category does not exist.");
        }

        var query = dbContext.PointsOfInterest.AsNoTracking();
        var search = request.Search?.Trim();
        if (!string.IsNullOrEmpty(search))
        {
            query = query.Where(poi => poi.Name.Contains(search)
                || (poi.Address != null && poi.Address.Contains(search)));
        }
        if (request.CategoryId.HasValue)
        {
            query = query.Where(poi => poi.CategoryId == request.CategoryId.Value);
        }
        if (request.Status is not null)
        {
            var status = request.Status == ListManagedPoisQuery.StatusActive
                ? PointOfInterestStatus.Active : PointOfInterestStatus.Inactive;
            query = query.Where(poi => poi.Status == status);
        }

        var matchingCount = await query.LongCountAsync(cancellationToken);
        if (matchingCount > int.MaxValue)
        {
            return Result.Failure<ManagedPoiListDto>(ManagedPoiErrors.InvalidQuery,
                "The matching POI count is outside the supported pagination range.");
        }
        var totalCount = (int)matchingCount;
        // ValidationBehaviour rejects offsets beyond Int32 before reaching the query handler.
        var offset = checked((request.Page - 1) * request.PageSize);
        var rows = await query.OrderBy(poi => poi.Name).ThenBy(poi => poi.Id)
            .Skip(offset).Take(request.PageSize)
            .Select(poi => new ManagedPoiRow(
                poi.Id, poi.Name, poi.CategoryId, poi.Category.Name, poi.Address,
                poi.Latitude, poi.Longitude, poi.Status,
                poi.OpeningHours.OrderBy(hours => hours.DayOfWeek)
                    .Select(hours => new PoiOpeningHourDto(hours.DayOfWeek, hours.OpenTime, hours.CloseTime, hours.IsClosed))
                    .ToList()))
            .ToListAsync(cancellationToken);

        // Format BIGINT only after the bounded SQL projection has materialized.
        var items = rows.Select(row => new ManagedPoiListItemDto(
            row.Id.ToString(CultureInfo.InvariantCulture), row.Name, row.CategoryId,
            row.CategoryName, row.Address, row.Latitude, row.Longitude, row.OpeningHours,
            row.Status.ToString())).ToArray();
        var totalPages = (int)(((long)totalCount + request.PageSize - 1) / request.PageSize);
        return Result.Success(new ManagedPoiListDto(
            request.Page, request.PageSize, totalCount, totalPages,
            new ManagedPoiMetadataDto(categories), items));
    }

    private static Result<ManagedPoiListDto> AccessRequired() =>
        Result.Failure<ManagedPoiListDto>(ManagedPoiErrors.AccessRequired,
            "You do not have permission to access this resource.");

    private sealed record ManagedPoiRow(
        long Id, string Name, int CategoryId, string CategoryName, string? Address,
        decimal Latitude, decimal Longitude, PointOfInterestStatus Status,
        IReadOnlyCollection<PoiOpeningHourDto> OpeningHours);
}
