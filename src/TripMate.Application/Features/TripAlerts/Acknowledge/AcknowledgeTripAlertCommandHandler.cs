using MediatR;

using Microsoft.EntityFrameworkCore;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Common.Models;
using TripMate.Application.Features.TripAlerts.Common;
using TripMate.Domain.Entities;

namespace TripMate.Application.Features.TripAlerts.Acknowledge;

public sealed class AcknowledgeTripAlertCommandHandler(
    IApplicationDbContext dbContext,
    IDateTimeProvider clock) : IRequestHandler<AcknowledgeTripAlertCommand, Result>
{
    public async Task<Result> Handle(
        AcknowledgeTripAlertCommand request,
        CancellationToken cancellationToken)
    {
        var ownerId = await dbContext.TripSessions
            .Where(session => session.Id == request.SessionId)
            .Select(session => (long?)session.TravelerUserId)
            .SingleOrDefaultAsync(cancellationToken);
        if (ownerId != request.TravelerUserId)
        {
            return Result.Failure(
                ownerId.HasValue ? TripAlertErrorCodes.AccessDenied : TripAlertErrorCodes.NotFound,
                ownerId.HasValue
                    ? "You do not have access to this alert."
                    : "The navigation session was not found.");
        }

        var incidentExists = await dbContext.Incidents.AsNoTracking().AnyAsync(
            incident => incident.Id == request.IncidentId && incident.SessionId == request.SessionId,
            cancellationToken);
        if (!incidentExists)
        {
            return NotFound();
        }

        var notification = await dbContext.Notifications.SingleOrDefaultAsync(
            candidate =>
                candidate.UserId == request.TravelerUserId
                && candidate.Type == Notification.TripAlertType
                && candidate.RelatedEntityType == Notification.IncidentRelatedEntityType
                && candidate.RelatedEntityId == request.IncidentId,
            cancellationToken);
        if (notification is null)
        {
            return NotFound();
        }

        if (notification.MarkRead(clock.UtcNow))
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }

        return Result.Success();
    }

    private static Result NotFound() => Result.Failure(
        TripAlertErrorCodes.NotFound,
        "The trip alert was not found.");
}