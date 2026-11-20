using System.Security.Cryptography;
using System.Text;

using MediatR;

using Microsoft.EntityFrameworkCore;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Common.Models;
using TripMate.Domain.Entities;
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

        var activeItinerariesCount = await dbContext.ItineraryItems
            .AsNoTracking()
            .Where(item => item.PointOfInterestId == request.PoiId && item.Itinerary.Status == Itinerary.ActiveStatus)
            .Select(item => item.ItineraryId)
            .Distinct()
            .CountAsync(cancellationToken);

        var activeRoutesCount = await dbContext.RouteSegments
            .AsNoTracking()
            .Where(route => (route.FromPoiId == request.PoiId || route.ToPoiId == request.PoiId)
                && route.Status == RouteStatus.Active)
            .CountAsync(cancellationToken);

        var openBookingsCount = await dbContext.ServiceBookings
            .AsNoTracking()
            .Where(booking => (booking.Status == ServiceBooking.PendingConfirmation || booking.Status == ServiceBooking.Confirmed)
                && dbContext.CommercialServices.Any(service => service.Id == booking.ServiceId && service.PoiId == request.PoiId))
            .CountAsync(cancellationToken);

        var rawToken = $"{request.PoiId}:{activeItinerariesCount}:{activeRoutesCount}:{openBookingsCount}";
        var snapshotToken = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(rawToken)));
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