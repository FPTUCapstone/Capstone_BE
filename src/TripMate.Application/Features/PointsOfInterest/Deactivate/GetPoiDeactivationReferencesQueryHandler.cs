using MediatR;

using Microsoft.EntityFrameworkCore;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Common.Models;
using TripMate.Domain.Enums;

namespace TripMate.Application.Features.PointsOfInterest.Deactivate;

public sealed class GetPoiDeactivationReferencesQueryHandler(
    IApplicationDbContext dbContext,
    ICurrentUserService currentUserService)
    : IRequestHandler<GetPoiDeactivationReferencesQuery, Result<PoiDeactivationReferencesDto>>
{
    public async Task<Result<PoiDeactivationReferencesDto>> Handle(
        GetPoiDeactivationReferencesQuery request,
        CancellationToken cancellationToken)
    {
        var actorId = currentUserService.UserId;
        if (!actorId.HasValue)
        {
            return Result.Failure<PoiDeactivationReferencesDto>(
                PoiDeactivationErrors.Forbidden, PoiDeactivationErrors.Msg126Message);
        }

        var isAdministrator = await dbContext.Users.AsNoTracking().AnyAsync(
            user => user.Id == actorId.Value
                && user.Status == AccountStatus.Active
                && user.Role == UserRole.Administrator,
            cancellationToken);

        if (!isAdministrator)
        {
            return Result.Failure<PoiDeactivationReferencesDto>(
                PoiDeactivationErrors.Forbidden, PoiDeactivationErrors.Msg126Message);
        }

        var poi = await dbContext.PointsOfInterest
            .AsNoTracking()
            .FirstOrDefaultAsync(p => p.Id == request.PoiId, cancellationToken);

        if (poi is null)
        {
            return Result.Failure<PoiDeactivationReferencesDto>(
                PoiDeactivationErrors.NotFound, PoiDeactivationErrors.NotFoundMessage);
        }

        if (poi.Status == PointOfInterestStatus.Inactive)
        {
            return Result.Failure<PoiDeactivationReferencesDto>(
                PoiDeactivationErrors.AlreadyInactive, PoiDeactivationErrors.AlreadyInactiveMessage);
        }

        var routeIds = await PoiReferenceSnapshot.ActiveRoutes(dbContext, request.PoiId)
            .Select(route => route.Id)
            .OrderBy(id => id)
            .ToListAsync(cancellationToken);
        var snapshot = await PoiReferenceSnapshot.ReadAsync(dbContext, request.PoiId, routeIds, cancellationToken);
        var activeItinerariesCount = snapshot.ItineraryIds.Count;
        var activeRoutesCount = snapshot.RouteIds.Count;
        var openBookingsCount = snapshot.BookingIds.Count;
        var snapshotToken = snapshot.CreateToken(request.PoiId);
        var hasActiveReferences = activeItinerariesCount > 0 || activeRoutesCount > 0 || openBookingsCount > 0;

        return Result.Success(new PoiDeactivationReferencesDto(
            request.PoiId,
            poi.Name,
            poi.Status.ToString(),
            activeItinerariesCount,
            activeRoutesCount,
            openBookingsCount,
            hasActiveReferences,
            snapshotToken,
            hasActiveReferences ? PoiDeactivationErrors.Msg194 : null,
            hasActiveReferences ? PoiDeactivationErrors.Msg194Message : null));
    }
}