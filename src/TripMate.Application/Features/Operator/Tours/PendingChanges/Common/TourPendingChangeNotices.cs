using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.Extensions.Logging;

using TripMate.Application.Common.Interfaces;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Application.Features.Operator.Tours.PendingChanges.Common;

/// <summary>
/// Notifies the submitting Tour Operator of an Approve/Reject decision, attempted once after the
/// decision transaction has already committed (spec TM-82 §3 point B/G5). Per spec §3 point H.D1,
/// there is no retry: a failed attempt is logged at Error level, tagged with the change-set id and
/// the acting user id, and never rethrown — the caller's already-returned success result is never
/// affected. The failed Notification entity is detached afterward (plan amendment B4) so a later,
/// unrelated SaveChangesAsync on the same DbContext does not try to save it again.
/// </summary>
public static class TourPendingChangeNotices
{
    public static async Task NotifyOperatorAsync(
        IApplicationDbContext dbContext,
        ILogger logger,
        long operatorUserId,
        string type,
        string title,
        long changeSetId,
        long actingUserId,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken)
    {
        EntityEntry<Notification> entry = dbContext.Notifications.Add(new Notification
        {
            UserId = operatorUserId,
            Channel = NotificationChannel.InApp,
            Type = type,
            Title = title,
            RelatedEntityType = Notification.TourPendingChangeRelatedEntityType,
            RelatedEntityId = changeSetId,
            Status = NotificationStatus.Pending,
            CreatedAtUtc = nowUtc.ToUniversalTime(),
        });

        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (Exception exception)
        {
            logger.LogError(exception,
                "Failed to deliver the pending-change notification for TourPendingChangeSet {ChangeSetId}, acted on by user {ActingUserId}.",
                changeSetId, actingUserId);
            entry.State = EntityState.Detached;
        }
    }
}