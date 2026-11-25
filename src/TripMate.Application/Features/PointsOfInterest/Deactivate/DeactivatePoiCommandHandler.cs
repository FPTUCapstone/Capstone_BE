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

        return await dbContext.ExecuteInSerializableTransactionAsync(async token =>
        {
            await poiDeactivationLock.AcquireAsync(request.PoiId, token);

            // Serializable retains the account read lock until the mutation commits or rolls back.
            var isAdministrator = await dbContext.Users.AsNoTracking().AnyAsync(
                user => user.Id == actorId.Value
                    && user.Status == AccountStatus.Active
                    && user.Role == UserRole.Administrator,
                token);
            if (!isAdministrator)
            {
                return Result.Failure<DeactivatePoiResultDto>(
                    PoiDeactivationErrors.Forbidden, PoiDeactivationErrors.Msg126Message);
            }

            var poi = await dbContext.PointsOfInterest
                .FirstOrDefaultAsync(p => p.Id == request.PoiId, token);

            if (poi is null)
            {
                return Result.Failure<DeactivatePoiResultDto>(
                    PoiDeactivationErrors.NotFound, PoiDeactivationErrors.NotFoundMessage);
            }

            if (poi.Status == PointOfInterestStatus.Inactive)
            {
                var replay = await FindReplayAsync(actorId.Value, request, token);
                return replay is not null
                    ? Result.Success(replay)
                    : Result.Failure<DeactivatePoiResultDto>(
                        PoiDeactivationErrors.AlreadyInactive, PoiDeactivationErrors.AlreadyInactiveMessage);
            }

            var activeRoutes = await PoiReferenceSnapshot.ActiveRoutes(dbContext, request.PoiId)
                .OrderBy(route => route.Id)
                .ToListAsync(token);
            var snapshot = await PoiReferenceSnapshot.ReadAsync(
                dbContext, request.PoiId, activeRoutes.Select(route => route.Id).ToArray(), token);
            var currentToken = snapshot.CreateToken(request.PoiId);
            var activeItinerariesCount = snapshot.ItineraryIds.Count;
            var activeRoutesCount = snapshot.RouteIds.Count;
            var openBookingsCount = snapshot.BookingIds.Count;

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

            var reason = request.Reason.Trim();
            var afterData = JsonSerializer.Serialize(
                new DeactivationAudit(
                    PointOfInterestStatus.Inactive.ToString(), reason, deactivatedRouteIds,
                    now, request.IdempotencyKey, currentToken),
                JsonSerializerOptions.Web);

            var auditLog = AuditLog.CreateRecordedOutcome(
                actorUserId: actorId.Value,
                actionType: AuditActionTypes.PoiDeactivated,
                affectedEntity: AuditEntityTypes.PointOfInterest,
                affectedEntityId: request.PoiId,
                createdAtUtc: now,
                result: AuditOutcome.Success,
                reason: reason,
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

    private async Task<DeactivatePoiResultDto?> FindReplayAsync(
        long actorId, DeactivatePoiCommand request, CancellationToken cancellationToken)
    {
        if (request.IdempotencyKey == Guid.Empty)
        {
            return null;
        }

        // A POI can only be deactivated once. The immutable audit record commits
        // atomically with that state change and provides the original response.
        var afterData = await dbContext.AuditLogs.AsNoTracking()
            .Where(audit => audit.AffectedEntity == AuditEntityTypes.PointOfInterest
                && audit.AffectedEntityId == request.PoiId
                && audit.ActorUserId == actorId
                && audit.ActionType == AuditActionTypes.PoiDeactivated
                && audit.Result == AuditOutcome.Success)
            .OrderByDescending(audit => audit.Id)
            .Select(audit => audit.AfterData)
            .FirstOrDefaultAsync(cancellationToken);
        if (afterData is null)
        {
            return null;
        }

        DeactivationAudit? saved;
        try
        {
            saved = JsonSerializer.Deserialize<DeactivationAudit>(afterData, JsonSerializerOptions.Web);
        }
        catch (JsonException)
        {
            return null;
        }

        return saved is { DeactivatedRouteIds: not null }
            && saved.IdempotencyKey == request.IdempotencyKey
            && string.Equals(saved.Reason, request.Reason.Trim(), StringComparison.Ordinal)
            && string.Equals(saved.ReferenceSnapshotToken, request.ReferenceSnapshotToken, StringComparison.OrdinalIgnoreCase)
            ? new DeactivatePoiResultDto(
                request.PoiId, saved.Status, saved.DeactivatedRouteIds.Count, saved.DeactivatedAtUtc,
                PoiDeactivationErrors.Msg193, PoiDeactivationErrors.Msg193Message)
            : null;
    }

    private sealed record DeactivationAudit(
        string Status,
        string Reason,
        IReadOnlyList<long> DeactivatedRouteIds,
        DateTimeOffset DeactivatedAtUtc,
        Guid IdempotencyKey,
        string ReferenceSnapshotToken);
}