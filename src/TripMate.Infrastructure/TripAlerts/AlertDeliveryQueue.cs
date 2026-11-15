using System.Data;
using System.Globalization;

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
    Guid ClaimToken,
    int AttemptCount,
    string Title,
    string Body,
    IReadOnlyDictionary<string, string> Data,
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
        await StopIneligibleEmergencyAsync(now, cancellationToken);
        return await ClaimTripAlertAsync(now, cancellationToken)
            ?? await ClaimNextEmergencyAsync(now, cancellationToken);
    }

    private async Task<ClaimedTripAlert?> ClaimTripAlertAsync(
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
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
        return new ClaimedTripAlert(notification.Id, claim, attempt,
            notification.Title ?? "TripMate", notification.Body ?? string.Empty,
            new Dictionary<string, string>
            {
                ["notificationId"] = notification.Id.ToString(CultureInfo.InvariantCulture),
                ["incidentId"] = incident.Id.ToString(CultureInfo.InvariantCulture),
                ["sessionId"] = incident.SessionId.ToString(CultureInfo.InvariantCulture),
                ["type"] = "TripAlert",
            },
            tokens);
    }

    /// <summary>
    /// UC-70 notices. A review notice is sent only while the review still needs attention, and a
    /// token is read for the notification's current owner, so a reassigned device is never used.
    /// </summary>
    private async Task<ClaimedTripAlert?> ClaimNextEmergencyAsync(
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        // Each undeliverable notice is stopped as it is met, so this ends.
        while (true)
        {
            var (claimed, stopped) = await ClaimEmergencyAsync(now, cancellationToken);
            if (!stopped)
            {
                return claimed;
            }
        }
    }

    private async Task<(ClaimedTripAlert? Claimed, bool Stopped)> ClaimEmergencyAsync(
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var claim = Guid.NewGuid();
        var expires = now + options.DeliveryLease;
        await using IDbContextTransaction transaction = await efContext.Database
            .BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        var affected = await efContext.Database.ExecuteSqlRawAsync("""
            ;WITH candidate AS (
                SELECT TOP (1) work.*
                FROM trip.AlertDeliveryWork work WITH (UPDLOCK, READPAST, ROWLOCK)
                JOIN dbo.Notifications notification ON notification.notification_id = work.notification_id
                LEFT JOIN trip.WeatherSafetyReviews review
                    ON notification.[type] IN ({3}, {4})
                   AND review.review_id = notification.related_entity_id
                WHERE notification.[type] IN ({3}, {4}, {5}, {6})
                  AND notification.[status] IN ({7}, {8})
                  AND work.next_attempt_at IS NOT NULL
                  AND work.next_attempt_at <= {0}
                  AND work.attempt_count < 3
                  AND (work.claim_expires_at IS NULL OR work.claim_expires_at <= {0})
                  AND (notification.[type] NOT IN ({3}, {4}) OR review.[status] IN ({9}, {10}))
                  AND EXISTS (
                      SELECT 1 FROM dbo.PushDeviceTokens token
                      WHERE token.user_id = notification.user_id
                        AND token.deactivated_at IS NULL)
                ORDER BY work.next_attempt_at, work.notification_id
            )
            UPDATE candidate
            SET claim_token = {1}, claim_expires_at = {2}, attempt_count = attempt_count + 1;
            """, [now, claim, expires,
                Notification.WeatherSafetyReviewCreatedType, Notification.WeatherSafetyReviewEscalatedType,
                Notification.EmergencyDepartureCancelledType, Notification.EmergencyDepartureStoppedType,
                nameof(NotificationStatus.Pending), nameof(NotificationStatus.Failed),
                WeatherSafetyReview.StatusPendingAcknowledgement, WeatherSafetyReview.StatusEscalated],
            cancellationToken);
        if (affected == 0)
        {
            await transaction.CommitAsync(cancellationToken);
            return (null, false);
        }

        var work = await dbContext.AlertDeliveryWork.AsNoTracking()
            .Where(item => item.ClaimToken == claim)
            .Select(item => new { item.NotificationId, item.AttemptCount })
            .SingleAsync(cancellationToken);
        var notification = await dbContext.Notifications.AsNoTracking()
            .SingleAsync(item => item.Id == work.NotificationId, cancellationToken);
        var tokens = await dbContext.PushDeviceTokens.AsNoTracking()
            .Where(token => token.UserId == notification.UserId && token.DeactivatedAtUtc == null)
            .Select(token => token.ProtectedToken)
            .ToArrayAsync(cancellationToken);
        var data = await BuildEmergencyDataAsync(notification, cancellationToken);
        if (data is null)
        {
            // The notice points at nothing it can name: retrying can never help, and leaving it
            // claimable would block every later notice.
            await efContext.Database.ExecuteSqlRawAsync("""
                UPDATE trip.AlertDeliveryWork
                SET next_attempt_at = NULL, claim_token = NULL, claim_expires_at = NULL
                WHERE notification_id = {0};
                UPDATE dbo.Notifications SET [status] = {1} WHERE notification_id = {0};
                """, [notification.Id, nameof(NotificationStatus.Failed)], cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return (null, true);
        }

        await transaction.CommitAsync(cancellationToken);
        return (new ClaimedTripAlert(notification.Id, claim, work.AttemptCount,
            notification.Title ?? "TripMate", notification.Body ?? string.Empty, data, tokens), false);
    }

    /// <summary>The frozen UC-70 data contract: identifiers and versions only.</summary>
    private async Task<IReadOnlyDictionary<string, string>?> BuildEmergencyDataAsync(
        Notification notification,
        CancellationToken cancellationToken)
    {
        if (notification.RelatedEntityId is not long relatedId)
        {
            return null;
        }

        if (notification.Type is Notification.WeatherSafetyReviewCreatedType
            or Notification.WeatherSafetyReviewEscalatedType)
        {
            var review = await dbContext.WeatherSafetyReviews.AsNoTracking()
                .Where(item => item.Id == relatedId)
                .Select(item => new { item.Id, item.TourScheduleId, item.TourSchedule.TourId })
                .SingleOrDefaultAsync(cancellationToken);
            if (review is null)
            {
                return null;
            }

            return new Dictionary<string, string>
            {
                ["type"] = EmergencyPushContract.ReviewType,
                ["reviewId"] = review.Id.ToString(CultureInfo.InvariantCulture),
                ["tourId"] = review.TourId.ToString(CultureInfo.InvariantCulture),
                ["scheduleId"] = review.TourScheduleId.ToString(CultureInfo.InvariantCulture),
                ["changeType"] = notification.Type == Notification.WeatherSafetyReviewCreatedType
                    ? WeatherSafetyReviewChangeTypes.Created
                    : WeatherSafetyReviewChangeTypes.Escalated,
                ["issuedAtUtc"] = notification.CreatedAtUtc.UtcDateTime.ToString("O", CultureInfo.InvariantCulture),
            };
        }

        long scheduleId = await dbContext.Bookings.AsNoTracking()
            .Where(item => item.Id == relatedId)
            .Select(item => item.TourScheduleId)
            .SingleOrDefaultAsync(cancellationToken);
        var operation = await dbContext.EmergencyTourCancellationOperations.AsNoTracking()
            .Where(item => item.TourScheduleId == scheduleId)
            .Select(item => new { item.Id, item.CommittedAtUtc })
            .SingleOrDefaultAsync(cancellationToken);
        if (operation is null)
        {
            return null;
        }

        return new Dictionary<string, string>
        {
            ["type"] = EmergencyPushContract.BookingType,
            ["bookingId"] = relatedId.ToString(CultureInfo.InvariantCulture),
            ["scheduleId"] = scheduleId.ToString(CultureInfo.InvariantCulture),
            ["operationId"] = operation.Id.ToString(CultureInfo.InvariantCulture),
            ["action"] = notification.Type == Notification.EmergencyDepartureStoppedType
                ? EmergencyPushContract.StopAction
                : EmergencyPushContract.CancelAction,
            ["committedAtUtc"] = operation.CommittedAtUtc.UtcDateTime.ToString("O", CultureInfo.InvariantCulture),
        };
    }

    /// <summary>
    /// Ends UC-70 work that can no longer be delivered: already sent, an acknowledged or closed
    /// review, no active device (the notice stays readable through REST), or exhausted attempts.
    /// </summary>
    private Task StopIneligibleEmergencyAsync(
        DateTimeOffset now,
        CancellationToken cancellationToken) =>
        efContext.Database.ExecuteSqlRawAsync("""
            UPDATE work
            SET next_attempt_at = NULL, claim_token = NULL, claim_expires_at = NULL
            FROM trip.AlertDeliveryWork work
            JOIN dbo.Notifications notification ON notification.notification_id = work.notification_id
            LEFT JOIN trip.WeatherSafetyReviews review
                ON notification.[type] IN ({1}, {2})
               AND review.review_id = notification.related_entity_id
            WHERE notification.[type] IN ({1}, {2}, {3}, {4})
              AND work.next_attempt_at IS NOT NULL
              AND (
                  notification.[status] IN ({5}, {6})
                  OR (notification.[type] IN ({1}, {2}) AND (review.[status] IS NULL OR review.[status] NOT IN ({7}, {8})))
                  OR NOT EXISTS (
                      SELECT 1 FROM dbo.PushDeviceTokens token
                      WHERE token.user_id = notification.user_id AND token.deactivated_at IS NULL)
                  OR (work.attempt_count >= 3 AND work.claim_expires_at <= {0})
              );
            """, [now,
                Notification.WeatherSafetyReviewCreatedType, Notification.WeatherSafetyReviewEscalatedType,
                Notification.EmergencyDepartureCancelledType, Notification.EmergencyDepartureStoppedType,
                nameof(NotificationStatus.Sent), nameof(NotificationStatus.Read),
                WeatherSafetyReview.StatusPendingAcknowledgement, WeatherSafetyReview.StatusEscalated],
            cancellationToken);

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