using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

using MediatR;

using Microsoft.EntityFrameworkCore;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Common.Models;
using TripMate.Domain.Common;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Application.Features.PointsOfInterest.Deactivate;

public sealed class DeactivatePoiCommandHandler(
    IApplicationDbContext dbContext,
    ICurrentUserService currentUserService,
    IDateTimeProvider dateTimeProvider,
    IPoiDeactivationLock poiDeactivationLock)
    : IRequestHandler<DeactivatePoiCommand, Result<DeactivatePoiResultDto>>
{
    public async Task<Result<DeactivatePoiResultDto>> Handle(
        DeactivatePoiCommand request,
        CancellationToken cancellationToken)
    {
        var actorId = currentUserService.UserId;
        if (!actorId.HasValue)
        {
            return Result.Failure<DeactivatePoiResultDto>(
                PoiDeactivationErrors.Forbidden, PoiDeactivationErrors.Msg126Message);
        }

        var isAdministrator = await dbContext.Users.AsNoTracking().AnyAsync(
            user => user.Id == actorId.Value
                && user.Status == AccountStatus.Active
                && user.Role == UserRole.Administrator,
            cancellationToken);

        if (!isAdministrator)
        {
            return Result.Failure<DeactivatePoiResultDto>(
                PoiDeactivationErrors.Forbidden, PoiDeactivationErrors.Msg126Message);
        }

        // Acquire lock coordinating with TM-225 provisioning and other POI mutations
        await poiDeactivationLock.AcquireAsync(request.PoiId, cancellationToken);

        return await dbContext.ExecuteInSerializableTransactionAsync(async token =>
        {
            var poi = await dbContext.PointsOfInterest
                .FirstOrDefaultAsync(p => p.Id == request.PoiId, token);

            if (poi is null)
            {
                return Result.Failure<DeactivatePoiResultDto>(
                    PoiDeactivationErrors.NotFound, PoiDeactivationErrors.NotFoundMessage);
            }

            if (poi.Status == PointOfInterestStatus.Inactive)
            {
                return Result.Failure<DeactivatePoiResultDto>(
                    PoiDeactivationErrors.AlreadyInactive, PoiDeactivationErrors.AlreadyInactiveMessage);
            }

            // Concurrency check (Abnormal case 6.a2): verify references haven't changed since preview
            var activeItinerariesCount = await dbContext.ItineraryItems
                .Where(item => item.PointOfInterestId == request.PoiId && item.Itinerary.Status == Itinerary.ActiveStatus)
                .Select(item => item.ItineraryId)
                .Distinct()
                .CountAsync(token);

            var activeRoutes = await dbContext.RouteSegments
                .Where(route => (route.FromPoiId == request.PoiId || route.ToPoiId == request.PoiId)
                    && route.Status == RouteStatus.Active)
                .ToListAsync(token);
            var activeRoutesCount = activeRoutes.Count;

            var openBookingsCount = await dbContext.ServiceBookings
                .Where(booking => (booking.Status == ServiceBooking.PendingConfirmation || booking.Status == ServiceBooking.Confirmed)
                    && dbContext.CommercialServices.Any(service => service.Id == booking.ServiceId && service.PoiId == request.PoiId))
                .CountAsync(token);

            var rawToken = $"{request.PoiId}:{activeItinerariesCount}:{activeRoutesCount}:{openBookingsCount}";
            var currentToken = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(rawToken)));

            if (!string.Equals(currentToken, request.ReferenceSnapshotToken, StringComparison.OrdinalIgnoreCase))
            {
                var metadata = new Dictionary<string, object?>
                {
                    ["messageCode"] = PoiDeactivationErrors.Msg194,
                    ["activeItinerariesCount"] = activeItinerariesCount,
                    ["activeRoutesCount"] = activeRoutesCount,
                    ["openCommercialBookingsCount"] = openBookingsCount,
                    ["referenceSnapshotToken"] = currentToken,
                };
                return Result.Failure<DeactivatePoiResultDto>(
                    PoiDeactivationErrors.ReferenceMismatch,
                    PoiDeactivationErrors.ReferenceMismatchMessage,
                    metadata);
            }

            var now = dateTimeProvider.UtcNow;

            // Transition POI status
            poi.TryDeactivate(now);

            // Cascade connecting routes to Inactive and flag for review (BR-122, PC-03)
            var deactivatedRouteIds = new List<long>();
            foreach (var route in activeRoutes)
            {
                route.DeactivateForEndpointReview(now, "Endpoint point of interest deactivated.");
                deactivatedRouteIds.Add(route.Id);
            }

            // Write immutable audit log (BR-130)
            var beforeData = JsonSerializer.Serialize(new
            {
                status = PointOfInterestStatus.Active.ToString(),
                activeItinerariesCount,
                activeRoutesCount,
                openBookingsCount,
            });

            var afterData = JsonSerializer.Serialize(new
            {
                status = PointOfInterestStatus.Inactive.ToString(),
                reason = request.Reason.Trim(),
                deactivatedRouteIds,
                deactivatedAtUtc = now,
            });

            var auditLog = AuditLog.CreateRecordedOutcome(
                actorUserId: actorId.Value,
                actionType: AuditActionTypes.PoiDeactivated,
                affectedEntity: AuditEntityTypes.PointOfInterest,
                affectedEntityId: request.PoiId,
                createdAtUtc: now,
                result: AuditOutcome.Success,
                reason: request.Reason.Trim(),
                beforeData: beforeData,
                afterData: afterData);

            dbContext.AuditLogs.Add(auditLog);

            await dbContext.SaveChangesAsync(token);

            return Result.Success(new DeactivatePoiResultDto(
                poi.Id,
                poi.Status.ToString(),
                deactivatedRouteIds.Count,
                now,
                PoiDeactivationErrors.Msg193,
                PoiDeactivationErrors.Msg193Message));
        }, cancellationToken);
    }
}