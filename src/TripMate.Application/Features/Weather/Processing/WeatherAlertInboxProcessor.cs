using System.Security.Cryptography;
using System.Text;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Features.TripAlerts.Common;
using TripMate.Application.Features.Weather.Emergency;
using TripMate.Application.Features.Weather.Matching;
using TripMate.Domain.Entities;

namespace TripMate.Application.Features.Weather.Processing;

public interface IWeatherAlertInboxProcessor
{
    Task ProcessInboxItemAsync(WeatherAlertInboxItem item, CancellationToken cancellationToken);
}

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
            string error = $"No normalizer registered for weather provider '{item.Provider}'.";
            logger.LogError(error);
            await inboxStore.MarkFailedAsync(item.Id, error, cancellationToken);
            return;
        }

        var normalizeResult = normalizer.Normalize(item.Payload, item.Provider, item.ProviderNotificationId, item.ReceivedAtUtc);
        if (normalizeResult.IsFailure)
        {
            logger.LogWarning("Failed to normalize weather alert {InboxId}: {Error}", item.Id, normalizeResult.ErrorMessage);
            await inboxStore.MarkFailedAsync(item.Id, normalizeResult.ErrorMessage ?? "Malformed payload", cancellationToken);
            return;
        }

        var events = normalizeResult.Value;
        if (events.Count == 0)
        {
            logger.LogInformation("No weather events extracted from inbox item {InboxId}. Marking processed.", item.Id);
            await inboxStore.MarkProcessedAsync(item.Id, cancellationToken);
            return;
        }

        string payloadHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(item.Payload))).ToLowerInvariant();
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
                    severity: evt.ProviderSeverity ?? "Moderate",
                    validFromUtc: evt.ValidFromUtc,
                    validToUtc: evt.ValidToUtc,
                    description: evt.Description,
                    originalAuthority: evt.OriginalAuthority,
                    originalSourceReference: evt.OriginalSourceReference,
                    officialVietnamRiskLevel: evt.OfficialVietnamRiskLevel,
                    verifiedDirective: evt.VerifiedDirective == WeatherVerifiedDirective.None ? null : evt.VerifiedDirective.ToString(),
                    geometryGeoJson: null,
                    rawPayloadHash: payloadHash,
                    ingestedAtUtc: timeProvider.GetUtcNow(),
                    revision: evt.Revision);

                if (evt.IsWithdrawn)
                {
                    weatherEvent.MarkWithdrawn(evt.UpdatedAtUtc);
                }

                context.WeatherEvents.Add(weatherEvent);
                processedEvents.Add((weatherEvent, evt));
            }
            else
            {
                // Replay check: older revision cannot overwrite newer evidence
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
                        verifiedDirective: evt.VerifiedDirective == WeatherVerifiedDirective.None ? null : evt.VerifiedDirective.ToString(),
                        geometryGeoJson: null,
                        rawPayloadHash: payloadHash,
                        updatedAtUtc: evt.UpdatedAtUtc,
                        revision: evt.Revision);
                }

                processedEvents.Add((existingEvent, evt));
            }
        }

        await context.SaveChangesAsync(cancellationToken);

        // Departure matching & policy evaluation
        if (departureFinder is not null)
        {
            var nowUtc = timeProvider.GetUtcNow();
            var createdReviews = new List<(WeatherSafetyReview Review, long OperatorUserId)>();

            await context.ExecuteInTransactionAsync(async token =>
            {
                createdReviews.Clear();
                foreach (var (weatherEventEntity, evt) in processedEvents)
                {
                    if (evt.IsWithdrawn)
                    {
                        continue;
                    }

                    var matchedDepartures = await departureFinder.FindAffectedDeparturesAsync(evt, token);
                    foreach (var matched in matchedDepartures)
                    {
                        var eval = WeatherEmergencyDecision.Evaluate(evt, matched.Impact, nowUtc);

                        if (eval.Outcome is WeatherDecisionOutcome.ActiveSafetyHold or WeatherDecisionOutcome.SafetyReview or WeatherDecisionOutcome.ReviewNeeded)
                        {
                            bool hasReview = await context.WeatherSafetyReviews
                                .AnyAsync(r => r.TourScheduleId == matched.Schedule.Id
                                               && ((r.Provider == evt.Provider && r.ProviderEventId == evt.ProviderEventId)
                                                   || r.Status == WeatherSafetyReview.StatusPendingAcknowledgement
                                                   || r.Status == WeatherSafetyReview.StatusEscalated), token);

                            if (!hasReview)
                            {
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

                        var existingEvaluation = await context.WeatherEventEvaluations
                            .FirstOrDefaultAsync(e => e.Provider == evt.Provider
                                                      && e.ProviderEventId == evt.ProviderEventId
                                                      && e.TourScheduleId == matched.Schedule.Id, token);

                        if (existingEvaluation is null)
                        {
                            context.WeatherEventEvaluations.Add(WeatherEventEvaluation.Record(
                                provider: evt.Provider,
                                providerEventId: evt.ProviderEventId,
                                tourScheduleId: matched.Schedule.Id,
                                decisionOutcome: eval.Outcome.ToString(),
                                reasonCode: eval.ReasonCode,
                                reasonDetails: eval.Details,
                                evaluatedAtUtc: nowUtc));
                        }
                        else
                        {
                            existingEvaluation.Reevaluate(
                                eval.Outcome.ToString(), eval.ReasonCode, eval.Details, nowUtc);
                        }
                    }
                }

                await context.SaveChangesAsync(token);

                // The review id exists only after the save above.
                foreach (var (review, operatorUserId) in createdReviews)
                {
                    EmergencyPushNotices.AddReviewNotice(
                        context, operatorUserId, review.Id, Notification.WeatherSafetyReviewCreatedType, nowUtc);
                }

                await context.SaveChangesAsync(token);
                return true;
            }, cancellationToken);

            if (reviewNotifier is not null)
            {
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
        }

        await inboxStore.MarkProcessedAsync(item.Id, cancellationToken);

        logger.LogInformation("Successfully processed weather inbox item {InboxId} with {EventCount} events.",
            item.Id, events.Count);
    }
}
