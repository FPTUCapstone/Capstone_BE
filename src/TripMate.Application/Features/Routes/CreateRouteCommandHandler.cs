using System.Text.Json;

using MediatR;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Common.Models;
using TripMate.Domain.Common;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Application.Features.Routes;

public sealed class CreateRouteCommandHandler(IApplicationDbContext dbContext,
    ICurrentUserService currentUserService, IDateTimeProvider dateTimeProvider,
    ILogger<CreateRouteCommandHandler>? logger = null)
    : IRequestHandler<CreateRouteCommand, Result<RouteDto>>
{
    public async Task<Result<RouteDto>> Handle(CreateRouteCommand request, CancellationToken cancellationToken)
        => await HandleAttempt(request, cancellationToken, 0);

    private async Task<Result<RouteDto>> HandleAttempt(CreateRouteCommand request,
        CancellationToken cancellationToken, int attempt)
    {
        if (currentUserService.UserId is not long userId || currentUserService.Role != nameof(UserRole.Staff))
            return Result.Failure<RouteDto>(RouteErrors.Msg126, RouteErrors.Forbidden);
        var validation = await new CreateRouteCommandValidator().ValidateAsync(request, cancellationToken);
        if (!validation.IsValid)
            return Result.Failure<RouteDto>(validation.Errors[0].ErrorCode, validation.Errors[0].ErrorMessage,
                new Dictionary<string, object?>
                {
                    ["errors"] = validation.Errors.GroupBy(error => error.PropertyName)
                    .ToDictionary(group => JsonNamingPolicy.CamelCase.ConvertName(group.Key), group => group.Select(error => error.ErrorMessage).ToArray())
                });
        try
        {
            return await dbContext.ExecuteInSerializableTransactionAsync(async ct =>
            {
                // These authoritative reads run under SERIALIZABLE, so account/POI state cannot
                // change until the route and required audit record have committed together.
                var activeStaff = await dbContext.Users.AnyAsync(user => user.Id == userId
                    && user.Role == UserRole.Staff && user.Status == AccountStatus.Active, ct);
                if (!activeStaff) return Result.Failure<RouteDto>(RouteErrors.Msg126, RouteErrors.Forbidden);
                if (await dbContext.RouteSegments.AnyAsync(route => route.CreatedById == userId
                    && route.IdempotencyKey == request.IdempotencyKey, ct))
                    return Result.Failure<RouteDto>(RouteErrors.DuplicateSubmission, "This route creation has already been submitted.");
                var endpoints = await dbContext.PointsOfInterest.Where(poi =>
                    (poi.Id == request.FromPoiId || poi.Id == request.ToPoiId)
                    && poi.Status == PointOfInterestStatus.Active).ToListAsync(ct);
                if (endpoints.Count != 2)
                    return Result.Failure<RouteDto>(RouteErrors.Msg141, RouteErrors.InvalidEndpoints);
                var mode = Enum.Parse<TransportMode>(request.TransportMode!);
                if (await dbContext.RouteSegments.AnyAsync(route => route.FromPoiId == request.FromPoiId
                    && route.ToPoiId == request.ToPoiId && route.TransportMode == mode, ct))
                    return Result.Failure<RouteDto>(RouteErrors.DuplicateRoute, "A route with these endpoints and transport mode already exists.");
                var now = dateTimeProvider.UtcNow;
                var route = RouteSegment.Create(endpoints.Single(poi => poi.Id == request.FromPoiId),
                    endpoints.Single(poi => poi.Id == request.ToPoiId), request.DistanceKm!.Value,
                    request.EstimatedDurationMinutes!.Value, mode, request.RouteNote, userId, request.IdempotencyKey, now);
                dbContext.RouteSegments.Add(route);
                await dbContext.SaveChangesAsync(ct);
                var dto = RouteDto.From(route);
                dbContext.AuditLogs.Add(AuditLog.CreateRecordedOutcome(userId, AuditActionTypes.RouteCreate,
                    AuditEntityTypes.RouteSegment, route.Id, now, AuditOutcome.Success,
                    afterData: JsonSerializer.Serialize(dto, new JsonSerializerOptions(JsonSerializerDefaults.Web))));
                await dbContext.SaveChangesAsync(ct);
                return Result.Success(dto with { MessageCode = RouteErrors.Msg139, Message = RouteErrors.Created });
            }, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception) when (attempt < 2 && dbContext.IsDeadlock(exception))
        {
            dbContext.ClearTrackedEntities();
            return await HandleAttempt(request, cancellationToken, attempt + 1);
        }
        catch (Exception exception)
        {
            // Transaction disposal rolls back route and audit on every persistence failure.
            // Discard pending tracked changes so callers cannot accidentally save them later.
            dbContext.ClearTrackedEntities();
            logger?.LogError(exception, "Route creation failed; route and audit transaction was not confirmed.");
            return Result.Failure<RouteDto>(RouteErrors.Msg127, RouteErrors.Unavailable);
        }
    }
}