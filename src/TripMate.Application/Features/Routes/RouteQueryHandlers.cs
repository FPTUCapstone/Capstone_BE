using MediatR;

using Microsoft.EntityFrameworkCore;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Common.Models;
using TripMate.Domain.Enums;

namespace TripMate.Application.Features.Routes;

public sealed class ListRoutesQueryHandler(IApplicationDbContext dbContext, ICurrentUserService currentUserService)
    : IRequestHandler<ListRoutesQuery, Result<RouteListDto>>
{
    public async Task<Result<RouteListDto>> Handle(ListRoutesQuery request, CancellationToken cancellationToken)
    {
        if (!await RouteAccess.IsActiveStaff(dbContext, currentUserService, cancellationToken))
            return Result.Failure<RouteListDto>(RouteErrors.Msg126, RouteErrors.Forbidden);
        if (request.Page < 1 || request.PageSize is < 1 or > 100)
            return Result.Failure<RouteListDto>(RouteErrors.InvalidInput, "Use a positive page and a page size between 1 and 100.");
        var routes = dbContext.RouteSegments.AsNoTracking();
        var count = await routes.CountAsync(cancellationToken);
        var offset = ((long)request.Page - 1) * request.PageSize;
        if (offset > int.MaxValue) return Result.Failure<RouteListDto>(RouteErrors.InvalidInput, "The requested page is out of range.");
        var items = await routes.Include(route => route.FromPoi).Include(route => route.ToPoi)
            .OrderByDescending(route => route.CreatedAtUtc).ThenByDescending(route => route.Id)
            .Skip((int)offset).Take(request.PageSize).ToListAsync(cancellationToken);
        return Result.Success(new RouteListDto(items.Select(RouteDto.From).ToArray(), request.Page, request.PageSize, count));
    }
}

public sealed class RouteOptionsQueryHandler(IApplicationDbContext dbContext, ICurrentUserService currentUserService)
    : IRequestHandler<RouteOptionsQuery, Result<RouteOptionsDto>>
{
    public async Task<Result<RouteOptionsDto>> Handle(RouteOptionsQuery request, CancellationToken cancellationToken)
    {
        if (!await RouteAccess.IsActiveStaff(dbContext, currentUserService, cancellationToken))
            return Result.Failure<RouteOptionsDto>(RouteErrors.Msg126, RouteErrors.Forbidden);
        var pois = await dbContext.PointsOfInterest.AsNoTracking().Where(poi => poi.Status == PointOfInterestStatus.Active)
            .OrderBy(poi => poi.Name).ThenBy(poi => poi.Id)
            .Select(poi => new RoutePoiOption(poi.Id, poi.Name, poi.Latitude, poi.Longitude)).ToListAsync(cancellationToken);
        return Result.Success(new RouteOptionsDto(pois, Enum.GetNames<TransportMode>()));
    }
}

internal static class RouteAccess
{
    internal static Task<bool> IsActiveStaff(IApplicationDbContext dbContext, ICurrentUserService currentUserService, CancellationToken ct) =>
        currentUserService.UserId is long userId && currentUserService.Role == nameof(UserRole.Staff)
            ? dbContext.Users.AsNoTracking().AnyAsync(user => user.Id == userId
                && user.Role == UserRole.Staff && user.Status == AccountStatus.Active, ct)
            : Task.FromResult(false);
}