using MediatR;

using Microsoft.EntityFrameworkCore;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Common.Models;
using TripMate.Application.Features.TripAlerts.Common;
using TripMate.Domain.Entities;

namespace TripMate.Application.Features.TripAlerts.List;

public sealed class ListTripAlertsQueryHandler(IApplicationDbContext dbContext)
    : IRequestHandler<ListTripAlertsQuery, Result<IReadOnlyCollection<TripAlertResponse>>>
{
    public async Task<Result<IReadOnlyCollection<TripAlertResponse>>> Handle(
        ListTripAlertsQuery request,
        CancellationToken cancellationToken)
    {
        var ownerId = await dbContext.TripSessions.AsNoTracking()
            .Where(session => session.Id == request.SessionId)
            .Select(session => (long?)session.TravelerUserId)
            .SingleOrDefaultAsync(cancellationToken);
        if (!ownerId.HasValue)
        {
            return Result.Failure<IReadOnlyCollection<TripAlertResponse>>(
                TripAlertErrorCodes.NotFound,
                "The navigation session was not found.");
        }

        if (ownerId.Value != request.TravelerUserId)
        {
            return Result.Failure<IReadOnlyCollection<TripAlertResponse>>(
                TripAlertErrorCodes.AccessDenied,
                "You do not have access to these alerts.");
        }

        var incidents = await dbContext.Incidents.AsNoTracking()
            .Include(incident => incident.AffectedItem)
            .Where(incident => incident.SessionId == request.SessionId)
            .OrderByDescending(incident => incident.DetectedAtUtc)
            .ThenByDescending(incident => incident.Id)
            .ToArrayAsync(cancellationToken);
        if (incidents.Length == 0)
        {
            return Result.Success<IReadOnlyCollection<TripAlertResponse>>([]);
        }

        var incidentIds = incidents.Select(incident => incident.Id).ToArray();
        var notifications = await dbContext.Notifications.AsNoTracking()
            .Where(notification =>
                notification.UserId == request.TravelerUserId
                && notification.Type == Notification.TripAlertType
                && notification.RelatedEntityType == Notification.IncidentRelatedEntityType
                && notification.RelatedEntityId.HasValue
                && incidentIds.Contains(notification.RelatedEntityId.Value))
            .ToDictionaryAsync(notification => notification.RelatedEntityId!.Value, cancellationToken);
        var proposals = await dbContext.ReroutingEvents.AsNoTracking()
            .Where(proposal =>
                incidentIds.Contains(proposal.IncidentId)
                && proposal.Status == ReroutingEvent.StatusProposed)
            .GroupBy(proposal => proposal.IncidentId)
            .Select(group => new { IncidentId = group.Key, ProposalId = group.Max(item => item.Id) })
            .ToDictionaryAsync(item => item.IncidentId, item => (long?)item.ProposalId, cancellationToken);

        IReadOnlyCollection<TripAlertResponse> response = incidents
            .Where(incident => notifications.ContainsKey(incident.Id))
            .Select(incident => TripAlertResponse.From(
                incident,
                notifications[incident.Id],
                proposals.GetValueOrDefault(incident.Id)))
            .ToArray();
        return Result.Success(response);
    }
}