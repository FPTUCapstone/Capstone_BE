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

        var processedEvents = new List<ProcessedWeatherSignal>();
        var clearedEvents = new List<WeatherEvent>();

        foreach (var evt in events)
        {
            if (evt.IsWithdrawn && evt.ClearanceAlertPrefix is not null)
            {
                var cleared = await MarkMatchingEventsWithdrawnAsync(evt, cancellationToken);
                clearedEvents.AddRange(cleared);
                continue;
            }

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
                processedEvents.Add(new ProcessedWeatherSignal(evt, false, null, null, null));
                continue;
            }

            // Replay check: older evidence cannot overwrite newer evidence.
            if (existingEvent.UpdatedAtUtc.HasValue && evt.UpdatedAtUtc < existingEvent.UpdatedAtUtc)
            {
                logger.LogInformation("Ignoring stale replay for event {ProviderEventId}. Current updatedAt: {Current}, incoming: {Incoming}",
                    evt.ProviderEventId, existingEvent.UpdatedAtUtc, evt.UpdatedAtUtc);
                continue;
            }

            bool wasWithdrawn = existingEvent.IsWithdrawn;
            string previousSeverity = existingEvent.Severity;
            string? previousDescription = existingEvent.Description;
            DateTimeOffset? previousUpdatedAtUtc = existingEvent.UpdatedAtUtc;

            if (evt.IsWithdrawn)
            {
                existingEvent.MarkWithdrawn(evt.UpdatedAtUtc);
                clearedEvents.Add(existingEvent);
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

            processedEvents.Add(new ProcessedWeatherSignal(
                evt, wasWithdrawn, previousSeverity, previousDescription, previousUpdatedAtUtc));
        }

        await context.SaveChangesAsync(cancellationToken);

        if (clearedEvents.Count > 0)
        {
            await NotifyWithdrawnEventReviewsAsync(clearedEvents, cancellationToken);
        }

        if (departureFinder is not null && processedEvents.Count > 0)
        {
            await CreateReviewsAsync(processedEvents, cancellationToken);
        }

        await CompleteAsync(item, cancellationToken);
        logger.LogInformation("Successfully processed weather inbox item {InboxId} with {EventCount} events.",
            item.Id, events.Count);
    }

    private async Task<IReadOnlyList<WeatherEvent>> MarkMatchingEventsWithdrawnAsync(
        NormalizedWeatherEvent clearance,
        CancellationToken cancellationToken)
    {
        string prefix = clearance.ClearanceAlertPrefix!;
        string? suffix = clearance.ClearanceLocationSuffix;

        var activeCandidates = await context.WeatherEvents
            .Where(e => e.Provider == clearance.Provider
                && !e.IsWithdrawn
                && e.ProviderEventId != null
                && (e.ProviderEventId == clearance.ProviderEventId
                    || (e.ProviderEventId.StartsWith(prefix)
                        && (suffix == null || e.ProviderEventId.EndsWith(suffix)))))
            .ToListAsync(cancellationToken);

        if (activeCandidates.Count == 0)
        {
            return [];
        }

        var inWindow = activeCandidates
            .Where(e => e.ValidFromUtc <= clearance.UpdatedAtUtc
                && (!e.ValidToUtc.HasValue || clearance.UpdatedAtUtc <= e.ValidToUtc.Value))
            .ToList();
        var targets = inWindow.Count > 0
            ? inWindow
            : activeCandidates
                .Where(e => !e.ValidToUtc.HasValue || clearance.UpdatedAtUtc <= e.ValidToUtc.Value)
                .ToList();

        var marked = new List<WeatherEvent>();
        foreach (var target in targets)
        {
            if (target.UpdatedAtUtc.HasValue && clearance.UpdatedAtUtc < target.UpdatedAtUtc.Value)
            {
                continue;
            }

            target.MarkWithdrawn(clearance.UpdatedAtUtc);
            marked.Add(target);
        }

        return marked;
    }

    private async Task NotifyWithdrawnEventReviewsAsync(
        IReadOnlyCollection<WeatherEvent> clearedEvents,
        CancellationToken cancellationToken)
    {
        if (reviewNotifier is null)
        {
            return;
        }

        var eventIds = clearedEvents
            .Where(e => !string.IsNullOrWhiteSpace(e.ProviderEventId))
            .Select(e => e.ProviderEventId!)
            .Distinct()
            .ToList();
        if (eventIds.Count == 0)
        {
            return;
        }

        var nowUtc = timeProvider.GetUtcNow();
        var affectedReviews = await context.WeatherSafetyReviews.AsNoTracking()
            .Where(r => eventIds.Contains(r.ProviderEventId) && r.Status != WeatherSafetyReview.StatusClosed)
            .Select(r => new { r.Id, r.TourScheduleId, r.Provider, r.ProviderEventId, r.TourSchedule.Tour.OperatorUserId })
            .ToListAsync(cancellationToken);

        var clearedPairs = clearedEvents
            .Where(e => e.Provider is not null && e.ProviderEventId is not null)
            .Select(e => (e.Provider!, e.ProviderEventId!))
            .ToHashSet();

        foreach (var review in affectedReviews.Where(r => clearedPairs.Contains((r.Provider, r.ProviderEventId))))
        {
            await reviewNotifier.NotifyChangedAsync(
                review.OperatorUserId,
                new WeatherSafetyReviewChanged(
                    review.Id,
                    review.TourScheduleId,
                    WeatherSafetyReviewChangeTypes.Updated,
                    nowUtc),
                cancellationToken);
        }
    }

    private async Task CreateReviewsAsync(IEnumerable<ProcessedWeatherSignal> signals, CancellationToken cancellationToken)
    {
        var nowUtc = timeProvider.GetUtcNow();
        var createdReviews = new List<(WeatherSafetyReview Review, long OperatorUserId)>();
        var reopenedReviews = new List<(WeatherSafetyReview Review, long OperatorUserId)>();

        await context.ExecuteInTransactionAsync(async token =>
        {
            createdReviews.Clear();
            reopenedReviews.Clear();
            var slaMinutes = options?.Value.SafetyReviewAcknowledgementSlaMinutes ?? 30;

            foreach (var signal in signals.Where(candidate => !candidate.Normalized.IsWithdrawn))
            {
                var evt = signal.Normalized;
                foreach (var matched in await departureFinder!.FindAffectedDeparturesAsync(evt, token))
                {
                    var eval = WeatherEmergencyDecision.Evaluate(evt, matched.Impact, nowUtc);
                    if (eval.Outcome is not (WeatherDecisionOutcome.ActiveSafetyHold
                        or WeatherDecisionOutcome.SafetyReview or WeatherDecisionOutcome.ReviewNeeded))
                    {
                        continue;
                    }

                    var existingReview = await context.WeatherSafetyReviews
                        .FirstOrDefaultAsync(r => r.TourScheduleId == matched.Schedule.Id
                            && r.Provider == evt.Provider
                            && r.ProviderEventId == evt.ProviderEventId, token);

                    if (existingReview is not null)
                    {
                        if (ShouldReopenAcknowledgedReview(existingReview, signal)
                            && existingReview.ReopenForCheckpoint(
                                urgentNoticeSentAtUtc: nowUtc,
                                acknowledgementDeadlineUtc: nowUtc.AddMinutes(slaMinutes),
                                fallbackInstructions: eval.Details ?? eval.ReasonCode))
                        {
                            reopenedReviews.Add((existingReview, matched.Schedule.Tour.OperatorUserId));
                        }

                        continue;
                    }

                    bool hasOpenOtherReview = await context.WeatherSafetyReviews
                        .AnyAsync(r => r.TourScheduleId == matched.Schedule.Id
                            && (r.Status == WeatherSafetyReview.StatusPendingAcknowledgement
                                || r.Status == WeatherSafetyReview.StatusEscalated), token);
                    if (hasOpenOtherReview)
                    {
                        continue;
                    }

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

            foreach (var (review, operatorUserId) in reopenedReviews)
            {
                EmergencyPushNotices.AddReviewNotice(
                    context, operatorUserId, review.Id, Notification.WeatherSafetyReviewCreatedType, nowUtc);
            }

            if (createdReviews.Count > 0 || reopenedReviews.Count > 0)
            {
                await context.SaveChangesAsync(token);
            }

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

        foreach (var (review, operatorUserId) in reopenedReviews)
        {
            await reviewNotifier.NotifyChangedAsync(
                operatorUserId,
                new WeatherSafetyReviewChanged(
                    review.Id,
                    review.TourScheduleId,
                    WeatherSafetyReviewChangeTypes.Updated,
                    nowUtc),
                cancellationToken);
        }
    }

    private static bool ShouldReopenAcknowledgedReview(
        WeatherSafetyReview review,
        ProcessedWeatherSignal signal)
    {
        if (review.Status != WeatherSafetyReview.StatusAcknowledged)
        {
            return false;
        }

        var evt = signal.Normalized;
        if (review.AcknowledgedAtUtc.HasValue && evt.UpdatedAtUtc < review.AcknowledgedAtUtc.Value)
        {
            return false;
        }

        if (signal.WasWithdrawn || SeverityRank(evt.ProviderSeverity) > SeverityRank(signal.PreviousSeverity))
        {
            return true;
        }

        DateTimeOffset baseline = review.AcknowledgedAtUtc ?? review.UrgentNoticeSentAtUtc;
        if (evt.UpdatedAtUtc > baseline)
        {
            return true;
        }

        return evt.UpdatedAtUtc == baseline
            && (evt.NotificationStage == "START" || evt.Description != signal.PreviousDescription);
    }

    private static int SeverityRank(string? severity) => severity switch
    {
        WeatherEvent.SeverityLow => 1,
        WeatherEvent.SeverityModerate => 2,
        WeatherEvent.SeveritySevere => 3,
        WeatherEvent.SeverityExtreme => 4,
        _ => 0,
    };

    private sealed record ProcessedWeatherSignal(
        NormalizedWeatherEvent Normalized,
        bool WasWithdrawn,
        string? PreviousSeverity,
        string? PreviousDescription,
        DateTimeOffset? PreviousUpdatedAtUtc);

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