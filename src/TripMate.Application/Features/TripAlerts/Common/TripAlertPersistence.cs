using Microsoft.EntityFrameworkCore;

using TripMate.Application.Common.Interfaces;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Application.Features.TripAlerts.Common;

public sealed class TripAlertPersistence(IApplicationDbContext dbContext)
{
    public async Task<TripAlertResponse> CreateAsync(
        TripSession session,
        string incidentType,
        string sourceKey,
        DateTimeOffset detectedAtUtc,
        long? affectedItemId,
        Guid? clientEventId,
        string? description,
        CancellationToken cancellationToken)
    {
        var incident = Incident.CreateTripAlert(
            session,
            incidentType,
            sourceKey,
            detectedAtUtc,
            affectedItemId,
            clientEventId,
            NormalizeDescription(description));
        dbContext.Incidents.Add(incident);
        await dbContext.SaveChangesAsync(cancellationToken);

        var notification = new Notification
        {
            UserId = session.TravelerUserId,
            Channel = NotificationChannel.Push,
            Type = Notification.TripAlertType,
            Title = "TripMate trip alert",
            Body = "Open TripMate to review an update to your active trip.",
            RelatedEntityType = Notification.IncidentRelatedEntityType,
            RelatedEntityId = incident.Id,
            Status = NotificationStatus.Pending,
            CreatedAtUtc = detectedAtUtc.ToUniversalTime(),
        };
        dbContext.Notifications.Add(notification);
        await dbContext.SaveChangesAsync(cancellationToken);

        dbContext.AlertDeliveryWork.Add(AlertDeliveryWork.Create(notification, detectedAtUtc));
        await dbContext.SaveChangesAsync(cancellationToken);
        return TripAlertResponse.From(incident, notification, null);
    }

    public async Task<TripAlertResponse?> FindAsync(
        Incident incident,
        long travelerUserId,
        CancellationToken cancellationToken)
    {
        var notification = await dbContext.Notifications.AsNoTracking()
            .SingleOrDefaultAsync(candidate =>
                candidate.UserId == travelerUserId
                && candidate.Type == Notification.TripAlertType
                && candidate.RelatedEntityType == Notification.IncidentRelatedEntityType
                && candidate.RelatedEntityId == incident.Id,
                cancellationToken);
        return notification is null ? null : TripAlertResponse.From(incident, notification, null);
    }

    private static string? NormalizeDescription(string? description)
    {
        if (string.IsNullOrWhiteSpace(description))
        {
            return null;
        }

        var normalized = description.Trim();
        return normalized.Length <= Incident.DescriptionMaxLength
            ? normalized
            : normalized[..Incident.DescriptionMaxLength];
    }
}