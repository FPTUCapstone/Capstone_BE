using System.Data;

using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Options;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Features.TripAlerts.Common;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Infrastructure.TripAlerts;

internal sealed record ClaimedTripAlert(
    long NotificationId,
    long IncidentId,
    long SessionId,
    Guid ClaimToken,
    int AttemptCount,
    string Title,
    string Body,
    IReadOnlyCollection<string> ProtectedDeviceTokens);

internal sealed class AlertDeliveryQueue(
    IApplicationDbContext dbContext,
    IDateTimeProvider clock,
    IOptions<TripAlertOptions> options)
{
    private const int MaxAttempts = 3;
    private readonly TripAlertOptions options = options.Value;
    private readonly DbContext efContext = dbContext as DbContext
        ?? throw new InvalidOperationException("Alert delivery requires an EF Core database context.");

    public async Task<ClaimedTripAlert?> ClaimNextAsync(CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;
        await StopIneligibleAndExpiredAsync(now, cancellationToken);
        var claim = Guid.NewGuid();
        var expires = now + options.DeliveryLease;
        await using IDbContextTransaction transaction = await efContext.Database
            .BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        var affected = await efContext.Database.ExecuteSqlRawAsync("""
            ;WITH candidate AS (
                SELECT TOP (1) work.*
                FROM trip.AlertDeliveryWork work WITH (UPDLOCK, READPAST, ROWLOCK)
                JOIN dbo.Notifications notification ON notification.notification_id = work.notification_id
                JOIN trip.Incidents incident ON notification.related_entity_id = incident.incident_id
                JOIN trip.TripSessions session ON incident.session_id = session.session_id
                WHERE notification.[type] = {3}
                  AND notification.related_entity_type = {4}
                  AND notification.[status] IN ({5}, {6})
                  AND work.next_attempt_at IS NOT NULL
                  AND work.next_attempt_at <= {0}
                  AND work.attempt_count < 3
                  AND (work.claim_expires_at IS NULL OR work.claim_expires_at <= {0})
                  AND incident.resolved_at IS NULL
                  AND session.ended_at IS NULL
                  AND session.fsm_state <> {7}
                  AND EXISTS (
                      SELECT 1 FROM dbo.PushDeviceTokens token
                      WHERE token.user_id = notification.user_id
                        AND token.deactivated_at IS NULL)
                ORDER BY work.next_attempt_at, work.notification_id
            )
            UPDATE candidate
            SET claim_token = {1}, claim_expires_at = {2}, attempt_count = attempt_count + 1;
            """, [now, claim, expires, Notification.TripAlertType,
                Notification.IncidentRelatedEntityType,
                nameof(NotificationStatus.Pending), nameof(NotificationStatus.Failed),
                TripSession.CompletedState], cancellationToken);
        if (affected == 0)
        {
            await transaction.CommitAsync(cancellationToken);
            return null;
        }

        var notificationId = await dbContext.AlertDeliveryWork.AsNoTracking()
            .Where(work => work.ClaimToken == claim)
            .Select(work => work.NotificationId)
            .SingleAsync(cancellationToken);
        var notification = await dbContext.Notifications.AsNoTracking()
            .SingleAsync(item => item.Id == notificationId, cancellationToken);
        var incident = await dbContext.Incidents.AsNoTracking()
            .SingleAsync(item => item.Id == notification.RelatedEntityId, cancellationToken);
        var tokens = await dbContext.PushDeviceTokens.AsNoTracking()
            .Where(token => token.UserId == notification.UserId && token.DeactivatedAtUtc == null)
            .Select(token => token.ProtectedToken)
            .ToArrayAsync(cancellationToken);
        var attempt = await dbContext.AlertDeliveryWork.AsNoTracking()
            .Where(work => work.ClaimToken == claim)
            .Select(work => work.AttemptCount)
            .SingleAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new ClaimedTripAlert(notification.Id, incident.Id, incident.SessionId, claim, attempt,
            notification.Title ?? "TripMate", notification.Body ?? string.Empty, tokens);
    }

    public async Task CompleteAsync(
        ClaimedTripAlert alert,
        bool sent,
        CancellationToken cancellationToken) =>
        await CompleteAsync(
            alert,
            new TripAlertPushResult(sent, []),
            cancellationToken);

    public async Task CompleteAsync(
        ClaimedTripAlert alert,
        TripAlertPushResult result,
        CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;
        if (result.InvalidProtectedTokens.Count > 0)
        {
            var invalidTokens = result.InvalidProtectedTokens.ToArray();
            await dbContext.PushDeviceTokens
                .Where(token => token.DeactivatedAtUtc == null
                    && invalidTokens.Contains(token.ProtectedToken))
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(token => token.DeactivatedAtUtc, now)
                    .SetProperty(token => token.UpdatedAtUtc, now), cancellationToken);
        }

        DateTimeOffset? next = !result.Sent && alert.AttemptCount < MaxAttempts
            ? now + (alert.AttemptCount == 1 ? TimeSpan.FromMinutes(15) : TimeSpan.FromMinutes(30))
            : null;
        var nextAttemptParameter = new SqlParameter("nextAttemptAt", SqlDbType.DateTime2)
        {
            Value = next.HasValue ? next.Value.UtcDateTime : DBNull.Value,
        };
        await efContext.Database.ExecuteSqlRawAsync("""
            UPDATE work
            SET next_attempt_at = {2}, claim_token = NULL, claim_expires_at = NULL
            FROM trip.AlertDeliveryWork work
            WHERE work.notification_id = {0} AND work.claim_token = {1};

            UPDATE dbo.Notifications
            SET [status] = {3}, sent_at = CASE WHEN {3} = {5} THEN {4} ELSE sent_at END
            WHERE notification_id = {0} AND [status] <> {6};
            """, [alert.NotificationId, alert.ClaimToken, nextAttemptParameter,
                result.Sent ? nameof(NotificationStatus.Sent) : nameof(NotificationStatus.Failed), now,
                nameof(NotificationStatus.Sent), nameof(NotificationStatus.Read)], cancellationToken);
    }

    private Task StopIneligibleAndExpiredAsync(
        DateTimeOffset now,
        CancellationToken cancellationToken) =>
        efContext.Database.ExecuteSqlRawAsync("""
            UPDATE work
            SET next_attempt_at = NULL, claim_token = NULL, claim_expires_at = NULL
            FROM trip.AlertDeliveryWork work
            JOIN dbo.Notifications notification ON notification.notification_id = work.notification_id
            JOIN trip.Incidents incident ON notification.related_entity_id = incident.incident_id
            JOIN trip.TripSessions session ON incident.session_id = session.session_id
            WHERE notification.[type] = {1}
              AND (
                  notification.[status] IN ({4}, {5})
                  OR incident.resolved_at IS NOT NULL
                  OR session.ended_at IS NOT NULL
                  OR session.fsm_state = {2}
                  OR NOT EXISTS (
                      SELECT 1 FROM dbo.PushDeviceTokens token
                      WHERE token.user_id = notification.user_id AND token.deactivated_at IS NULL)
                  OR (work.attempt_count >= 3 AND work.claim_expires_at <= {0})
              );

            UPDATE notification
            SET [status] = {3}
            FROM dbo.Notifications notification
            JOIN trip.AlertDeliveryWork work ON work.notification_id = notification.notification_id
            WHERE notification.[type] = {1}
              AND notification.[status] <> {4}
              AND work.next_attempt_at IS NULL
              AND notification.[status] <> {5};
            """, [now, Notification.TripAlertType,
                TripSession.CompletedState,
                nameof(NotificationStatus.Failed), nameof(NotificationStatus.Read),
                nameof(NotificationStatus.Sent)], cancellationToken);
}