using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Features.TripAlerts.Common;
using TripMate.Application.Features.Weather.Emergency;
using TripMate.Application.Features.Weather.Matching;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Application.Features.Weather.Processing;

public interface IWeatherAlertInboxProcessor
{
    Task ProcessInboxItemAsync(WeatherAlertInboxItem item, CancellationToken cancellationToken);
}

/// <summary>
/// Turns one leased inbox item into weather evidence and, where a departure is affected, a
/// WeatherSafetyReview for the owning Operator. It never cancels or stops a departure.
/// Review uniqueness is UX_WeatherSafetyReviews_ScheduleEvent (schedule, provider, event).
/// </summary>
public sealed class WeatherAlertInboxProcessor(
    IApplicationDbContext context,
    IEnumerable<IWeatherAlertNormalizer> normalizers,
    IWeatherAlertInboxStore inboxStore,
    TimeProvider timeProvider,
    ILogger<WeatherAlertInboxProcessor> logger,
    IWeatherAffectedDepartureFinder? departureFinder = null,
    IOptions<WeatherAlertProcessingOptions>? options = null,
    IWeatherSafetyReviewNotifier? reviewNotifier = null) : IWeatherAlertInboxProcessor
{
    public async Task ProcessInboxItemAsync(WeatherAlertInboxItem item, CancellationToken cancellationToken)
    {
        var normalizer = normalizers.FirstOrDefault(n => n.CanHandle(item.Provider));
        if (normalizer is null)
        {
            logger.LogError("No normalizer registered for weather provider {Provider}.", item.Provider);
            await inboxStore.MarkFailedAsync(item, $"No normalizer registered for weather provider '{item.Provider}'.", cancellationToken);
            return;
        }

        var normalizeResult = normalizer.Normalize(item.Payload, item.Provider, item.ProviderNotificationId, item.ReceivedAtUtc);
        if (normalizeResult.IsFailure)
        {
            logger.LogWarning("Failed to normalize weather alert {InboxId}: {ErrorCode}", item.Id, normalizeResult.ErrorCode);
            await inboxStore.MarkFailedAsync(item, normalizeResult.ErrorMessage ?? "Malformed payload", cancellationToken);
            return;
        }

        var events = await WithMonitoredPointsAsync(normalizeResult.Value, cancellationToken);
        if (events.Count == 0)
        {
            logger.LogInformation("Inbox item {InboxId} carries no TripMate weather evidence. Marking processed.", item.Id);
            await CompleteAsync(item, cancellationToken);
            return;
        }

        var processedEvents = new List<(WeatherEvent EventEntity, NormalizedWeatherEvent Normalized)>();

        foreach (var evt in events)
        {
            var existingEvent = await context.WeatherEvents
                .FirstOrDefaultAsync(e => e.Provider == evt.Provider && e.ProviderEventId == evt.ProviderEventId, cancellationToken);

            if (existingEvent is null)
            {
                var weatherEvent = WeatherEvent.CreateOfficial(
                    provider: evt.Provider,
                    providerEventId: evt.ProviderEventId,
                    eventType: evt.Category ?? "SevereWeather",
                    severity: evt.ProviderSeverity ?? WeatherEvent.SeverityModerate,
                    validFromUtc: evt.ValidFromUtc,
                    validToUtc: evt.ValidToUtc,
                    description: evt.Description,
                    originalAuthority: evt.OriginalAuthority,
                    officialVietnamRiskLevel: evt.OfficialVietnamRiskLevel,
                    verifiedDirective: ToColumn(evt.VerifiedDirective),
                    ingestedAtUtc: timeProvider.GetUtcNow());

                if (evt.IsWithdrawn)
                {
                    weatherEvent.MarkWithdrawn(evt.UpdatedAtUtc);
                }

                context.WeatherEvents.Add(weatherEvent);
                processedEvents.Add((weatherEvent, evt));
                continue;
            }

            // Replay check: older evidence cannot overwrite newer evidence.
            if (existingEvent.UpdatedAtUtc.HasValue && evt.UpdatedAtUtc < existingEvent.UpdatedAtUtc)
            {
                logger.LogInformation("Ignoring stale replay for event {ProviderEventId}. Current updatedAt: {Current}, incoming: {Incoming}",
                    evt.ProviderEventId, existingEvent.UpdatedAtUtc, evt.UpdatedAtUtc);
                continue;
            }

            if (evt.IsWithdrawn)
            {
                existingEvent.MarkWithdrawn(evt.UpdatedAtUtc);
            }
            else
            {
                existingEvent.UpdateRevision(
                    validFromUtc: evt.ValidFromUtc,
                    validToUtc: evt.ValidToUtc,
                    severity: evt.ProviderSeverity ?? existingEvent.Severity,
                    description: evt.Description,
                    officialVietnamRiskLevel: evt.OfficialVietnamRiskLevel,
                    verifiedDirective: ToColumn(evt.VerifiedDirective),
                    updatedAtUtc: evt.UpdatedAtUtc);
            }

            processedEvents.Add((existingEvent, evt));
        }

        await context.SaveChangesAsync(cancellationToken);

        if (departureFinder is not null)
        {
            await CreateReviewsAsync(processedEvents.Select(processed => processed.Normalized), cancellationToken);
        }

        await CompleteAsync(item, cancellationToken);
        logger.LogInformation("Successfully processed weather inbox item {InboxId} with {EventCount} events.",
            item.Id, events.Count);
    }

    private async Task CreateReviewsAsync(IEnumerable<NormalizedWeatherEvent> events, CancellationToken cancellationToken)
    {
        var nowUtc = timeProvider.GetUtcNow();
        var createdReviews = new List<(WeatherSafetyReview Review, long OperatorUserId)>();

        await context.ExecuteInTransactionAsync(async token =>
        {
            createdReviews.Clear();
            foreach (var evt in events.Where(candidate => !candidate.IsWithdrawn))
            {
                foreach (var matched in await departureFinder!.FindAffectedDeparturesAsync(evt, token))
                {
                    var eval = WeatherEmergencyDecision.Evaluate(evt, matched.Impact, nowUtc);
                    if (eval.Outcome is not (WeatherDecisionOutcome.ActiveSafetyHold
                        or WeatherDecisionOutcome.SafetyReview or WeatherDecisionOutcome.ReviewNeeded))
                    {
                        continue;
                    }

                    bool hasReview = await context.WeatherSafetyReviews
                        .AnyAsync(r => r.TourScheduleId == matched.Schedule.Id
                                       && ((r.Provider == evt.Provider && r.ProviderEventId == evt.ProviderEventId)
                                           || r.Status == WeatherSafetyReview.StatusPendingAcknowledgement
                                           || r.Status == WeatherSafetyReview.StatusEscalated), token);
                    if (hasReview)
                    {
                        continue;
                    }

                    var slaMinutes = options?.Value.SafetyReviewAcknowledgementSlaMinutes ?? 30;
                    var review = WeatherSafetyReview.CreateHold(
                        tourScheduleId: matched.Schedule.Id,
                        provider: evt.Provider,
                        providerEventId: evt.ProviderEventId,
                        urgentNoticeSentAtUtc: nowUtc,
                        acknowledgementDeadlineUtc: nowUtc.AddMinutes(slaMinutes),
                        immediateInstructions: eval.Details ?? eval.ReasonCode);

                    context.WeatherSafetyReviews.Add(review);
                    createdReviews.Add((review, matched.Schedule.Tour.OperatorUserId));
                }
            }

            await context.SaveChangesAsync(token);

            // The review id exists only after the save above.
            foreach (var (review, operatorUserId) in createdReviews)
            {
                EmergencyPushNotices.AddReviewNotice(
                    context, operatorUserId, review.Id, Notification.WeatherSafetyReviewCreatedType, nowUtc);
                await AddTravelerWarningsAsync(review.TourScheduleId, nowUtc, token);
            }

            await context.SaveChangesAsync(token);
            return true;
        }, cancellationToken);

        if (reviewNotifier is null)
        {
            return;
        }

        foreach (var (review, operatorUserId) in createdReviews)
        {
            await reviewNotifier.NotifyChangedAsync(
                operatorUserId,
                new WeatherSafetyReviewChanged(
                    review.Id,
                    review.TourScheduleId,
                    WeatherSafetyReviewChangeTypes.Created,
                    nowUtc),
                cancellationToken);
        }
    }

    /// <summary>
    /// SRS UC-66 MSG185: every Traveler holding an open booking of the departure learns of the warning.
    /// In-app only, with generic copy; the warning changes no departure, booking or session state.
    /// </summary>
    private async Task AddTravelerWarningsAsync(long scheduleId, DateTimeOffset nowUtc, CancellationToken cancellationToken)
    {
        var bookings = await context.Bookings.AsNoTracking()
            .Where(booking => booking.TourScheduleId == scheduleId
                && (booking.Status == BookingStatus.PendingPayment || booking.Status == BookingStatus.Confirmed))
            .Select(booking => new { booking.Id, booking.TravelerUserId })
            .ToListAsync(cancellationToken);
        foreach (var booking in bookings)
        {
            context.Notifications.Add(new Notification
            {
                UserId = booking.TravelerUserId,
                Channel = NotificationChannel.InApp,
                Type = Notification.WeatherWarningType,
                Title = Notification.WeatherWarningTitle,
                Body = Notification.WeatherWarningBody,
                RelatedEntityType = Notification.BookingRelatedEntityType,
                RelatedEntityId = booking.Id,
                Status = NotificationStatus.Pending,
                CreatedAtUtc = nowUtc.ToUniversalTime(),
            });
        }
    }

    /// <summary>
    /// Location alerts name Tomorrow.io Location ids only. Each id is resolved to the TripMate
    /// point it monitors; an alert for a Location TripMate does not own carries no evidence.
    /// </summary>
    private async Task<IReadOnlyList<NormalizedWeatherEvent>> WithMonitoredPointsAsync(
        IReadOnlyList<NormalizedWeatherEvent> events,
        CancellationToken cancellationToken)
    {
        var locationIds = events.SelectMany(evt => evt.ProviderLocationIds).Distinct().ToList();
        if (locationIds.Count == 0)
        {
            return events.Where(evt => evt.HasSpatialScope).ToList();
        }

        var points = (await context.WeatherLocationMappings
                .AsNoTracking()
                .Where(mapping => mapping.IsActive && locationIds.Contains(mapping.TomorrowLocationId))
                .Select(mapping => new { mapping.TomorrowLocationId, mapping.Latitude, mapping.Longitude })
                .ToListAsync(cancellationToken))
            .ToDictionary(mapping => mapping.TomorrowLocationId, mapping => new WeatherPoint(mapping.Latitude, mapping.Longitude));

        return events
            .Select(evt => evt with
            {
                MonitoredPoints = evt.ProviderLocationIds
                    .Where(points.ContainsKey)
                    .Select(id => points[id])
                    .ToList(),
            })
            .Where(evt => evt.HasSpatialScope)
            .ToList();
    }

    private async Task CompleteAsync(WeatherAlertInboxItem item, CancellationToken cancellationToken)
    {
        if (!await inboxStore.MarkProcessedAsync(item, cancellationToken))
        {
            logger.LogWarning("Inbox item {InboxId} lease was taken over before completion; the new lease owner completes it.", item.Id);
        }
    }

    private static string? ToColumn(WeatherVerifiedDirective directive) =>
        directive == WeatherVerifiedDirective.None ? null : directive.ToString();
}