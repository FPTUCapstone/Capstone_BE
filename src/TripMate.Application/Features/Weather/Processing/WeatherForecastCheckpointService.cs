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

public interface IWeatherForecastCheckpointService
{
    Task<int> EvaluateDueCheckpointsAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Re-evaluates open Tomorrow.io <c>CustomInsight</c> reviews against hourly forecasts at configured
/// pre-departure checkpoints (24 h, 12 h, 6 h). Clears withdrawn forecasts that never reached
/// <c>START</c>/<c>END</c> and reopens acknowledged reviews when hazardous weather persists.
/// </summary>
public sealed class WeatherForecastCheckpointService(
    IApplicationDbContext context,
    ITomorrowAlertsClient alertsClient,
    IWeatherDepartureImpactLoader impactLoader,
    TimeProvider timeProvider,
    IOptions<WeatherAlertProcessingOptions> options,
    ILogger<WeatherForecastCheckpointService> logger,
    IWeatherSafetyReviewNotifier? reviewNotifier = null) : IWeatherForecastCheckpointService
{
    private const string CustomInsightEventType = "CustomInsight";

    private enum ForecastCheckOutcome
    {
        Clear,
        Hazardous,
        Inconclusive,
        ProviderAborted,
    }

    public async Task<int> EvaluateDueCheckpointsAsync(CancellationToken cancellationToken = default)
    {
        var opts = options.Value;
        if (!opts.Enabled)
        {
            return 0;
        }

        int[] checkpointHours = (opts.ForecastCheckpointHoursBeforeDeparture ?? [])
            .Where(h => h > 0)
            .Distinct()
            .OrderByDescending(h => h)
            .ToArray();
        if (checkpointHours.Length == 0)
        {
            return 0;
        }

        var nowUtc = timeProvider.GetUtcNow();
        var horizonUtc = nowUtc.AddHours(checkpointHours[0]);

        var preDepartureReviews = await context.WeatherSafetyReviews
            .Include(r => r.TourSchedule)
                .ThenInclude(s => s.Tour)
                    .ThenInclude(t => t.ItineraryItems)
                        .ThenInclude(i => i.Poi)
            .Where(r => r.Status != WeatherSafetyReview.StatusClosed
                && r.Provider == WeatherProviders.TomorrowIo
                && r.TourSchedule.Status == TourScheduleStatus.Scheduled
                && r.TourSchedule.StartAtUtc > nowUtc
                && r.TourSchedule.StartAtUtc <= horizonUtc)
            .ToListAsync(cancellationToken);

        if (preDepartureReviews.Count == 0)
        {
            return 0;
        }

        var candidateEventIds = preDepartureReviews.Select(r => r.ProviderEventId).Distinct().ToList();
        var activeEvents = await context.WeatherEvents
            .Where(e => e.Provider == WeatherProviders.TomorrowIo
                && e.ProviderEventId != null
                && candidateEventIds.Contains(e.ProviderEventId)
                && e.EventType == CustomInsightEventType
                && !e.IsWithdrawn)
            .ToDictionaryAsync(e => e.ProviderEventId!, StringComparer.Ordinal, cancellationToken);

        if (activeEvents.Count == 0)
        {
            return 0;
        }

        var dueByEvent = new Dictionary<string, List<WeatherSafetyReview>>(StringComparer.Ordinal);
        foreach (var review in preDepartureReviews)
        {
            if (!activeEvents.TryGetValue(review.ProviderEventId, out var weatherEvent))
            {
                continue;
            }

            DateTimeOffset? dueCheckpointUtc = ResolveDueCheckpointUtc(review.TourSchedule.StartAtUtc, checkpointHours, nowUtc);
            if (!dueCheckpointUtc.HasValue)
            {
                continue;
            }

            DateTimeOffset eventBaselineUtc = weatherEvent.UpdatedAtUtc ?? weatherEvent.IngestedAtUtc;
            DateTimeOffset lastEvaluatedUtc = review.UrgentNoticeSentAtUtc >= eventBaselineUtc
                ? review.UrgentNoticeSentAtUtc
                : eventBaselineUtc;

            if (lastEvaluatedUtc >= dueCheckpointUtc.Value)
            {
                continue;
            }

            if (!dueByEvent.TryGetValue(review.ProviderEventId, out var list))
            {
                list = [];
                dueByEvent[review.ProviderEventId] = list;
            }

            list.Add(review);
        }

        if (dueByEvent.Count == 0)
        {
            return 0;
        }

        // Load all open scheduled reviews referencing these events (including active or further-out
        // departures) so clearing one tour never prematurely withdraws a shared WeatherEvent.
        var dueEventIds = dueByEvent.Keys.ToList();
        var allOpenReviews = await context.WeatherSafetyReviews
            .Include(r => r.TourSchedule)
                .ThenInclude(s => s.Tour)
                    .ThenInclude(t => t.ItineraryItems)
                        .ThenInclude(i => i.Poi)
            .Where(r => r.Status != WeatherSafetyReview.StatusClosed
                && r.Provider == WeatherProviders.TomorrowIo
                && dueEventIds.Contains(r.ProviderEventId)
                && r.TourSchedule.Status == TourScheduleStatus.Scheduled
                && r.TourSchedule.EndAtUtc > nowUtc)
            .ToListAsync(cancellationToken);

        var allOpenByEvent = allOpenReviews
            .GroupBy(r => r.ProviderEventId, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);

        var forecastCache = new Dictionary<WeatherPoint, TomorrowHourlyForecastResponse>();
        var signalRTargets = new List<(long OperatorUserId, long ReviewId, long TourScheduleId)>();
        var slaMinutes = Math.Max(1, opts.SafetyReviewAcknowledgementSlaMinutes);
        bool anyDbChange = false;
        int updatedReviews = 0;

        foreach (var (providerEventId, dueReviews) in dueByEvent)
        {
            var weatherEvent = activeEvents[providerEventId];
            var eventReviews = allOpenByEvent.GetValueOrDefault(providerEventId) ?? dueReviews;
            bool hasInProgressTour = eventReviews.Any(r => r.TourSchedule.StartAtUtc <= nowUtc);

            var dueOutcomes = new Dictionary<long, ForecastCheckOutcome>(dueReviews.Count);
            bool aborted = false;

            foreach (var review in dueReviews)
            {
                var outcome = await EvaluateScheduleAsync(review.TourSchedule, forecastCache, opts, cancellationToken);
                if (outcome == ForecastCheckOutcome.ProviderAborted)
                {
                    aborted = true;
                    break;
                }

                dueOutcomes[review.Id] = outcome;
            }

            if (aborted)
            {
                logger.LogWarning("Aborting forecast checkpoint sweep because Tomorrow.io is unavailable or unauthorized.");
                break;
            }

            bool anyHazardous = dueOutcomes.Values.Any(o => o == ForecastCheckOutcome.Hazardous);
            bool anyInconclusive = dueOutcomes.Values.Any(o => o == ForecastCheckOutcome.Inconclusive);

            // If every due review is clear and no active tour shares the event, verify any other
            // pre-departure tours sharing the same WeatherEvent before marking it withdrawn.
            if (!anyHazardous && !anyInconclusive && !hasInProgressTour)
            {
                var dueIds = dueReviews.Select(r => r.Id).ToHashSet();
                foreach (var sibling in eventReviews.Where(r => !dueIds.Contains(r.Id)))
                {
                    var siblingOutcome = await EvaluateScheduleAsync(sibling.TourSchedule, forecastCache, opts, cancellationToken);
                    if (siblingOutcome == ForecastCheckOutcome.ProviderAborted)
                    {
                        aborted = true;
                        break;
                    }

                    if (siblingOutcome == ForecastCheckOutcome.Hazardous)
                    {
                        anyHazardous = true;
                        break;
                    }

                    if (siblingOutcome == ForecastCheckOutcome.Inconclusive)
                    {
                        anyInconclusive = true;
                        break;
                    }
                }

                if (aborted)
                {
                    logger.LogWarning("Aborting forecast checkpoint sweep because Tomorrow.io is unavailable or unauthorized.");
                    break;
                }
            }

            if (!anyHazardous && !anyInconclusive && !hasInProgressTour)
            {
                weatherEvent.MarkWithdrawn(nowUtc);
                anyDbChange = true;
                updatedReviews += dueReviews.Count;

                foreach (var review in eventReviews)
                {
                    signalRTargets.Add((review.TourSchedule.Tour.OperatorUserId, review.Id, review.TourScheduleId));
                }

                logger.LogInformation(
                    "Forecast cleared at checkpoint for WeatherEvent {ProviderEventId}; marked withdrawn ({ReviewCount} open reviews refreshed).",
                    providerEventId, eventReviews.Count);
                continue;
            }

            if (anyHazardous)
            {
                weatherEvent.UpdateRevision(
                    validFromUtc: weatherEvent.ValidFromUtc,
                    validToUtc: weatherEvent.ValidToUtc,
                    severity: weatherEvent.Severity,
                    description: null,
                    officialVietnamRiskLevel: weatherEvent.OfficialVietnamRiskLevel,
                    verifiedDirective: weatherEvent.VerifiedDirective,
                    updatedAtUtc: nowUtc);
                anyDbChange = true;

                foreach (var review in dueReviews)
                {
                    if (dueOutcomes[review.Id] != ForecastCheckOutcome.Hazardous)
                    {
                        continue;
                    }

                    if (review.ReopenForCheckpoint(
                        urgentNoticeSentAtUtc: nowUtc,
                        acknowledgementDeadlineUtc: nowUtc.AddMinutes(slaMinutes),
                        fallbackInstructions: weatherEvent.Description))
                    {
                        long operatorUserId = review.TourSchedule.Tour.OperatorUserId;
                        EmergencyPushNotices.AddReviewNotice(
                            context,
                            operatorUserId,
                            review.Id,
                            Notification.WeatherSafetyReviewCreatedType,
                            nowUtc);
                        signalRTargets.Add((operatorUserId, review.Id, review.TourScheduleId));
                        updatedReviews++;
                    }
                }
            }
        }

        if (anyDbChange)
        {
            await context.SaveChangesAsync(cancellationToken);
        }

        if (reviewNotifier is not null && signalRTargets.Count > 0)
        {
            foreach (var target in signalRTargets.Distinct())
            {
                await reviewNotifier.NotifyChangedAsync(
                    target.OperatorUserId,
                    new WeatherSafetyReviewChanged(
                        target.ReviewId,
                        target.TourScheduleId,
                        WeatherSafetyReviewChangeTypes.Updated,
                        nowUtc),
                    cancellationToken);
            }
        }

        return updatedReviews;
    }

    private async Task<ForecastCheckOutcome> EvaluateScheduleAsync(
        TourSchedule schedule,
        Dictionary<WeatherPoint, TomorrowHourlyForecastResponse> forecastCache,
        WeatherAlertProcessingOptions opts,
        CancellationToken cancellationToken)
    {
        var impact = impactLoader.LoadImpact(schedule);
        var checks = new List<(WeatherPoint Point, DateTimeOffset WindowStart, DateTimeOffset WindowEnd)>();

        if (impact.MeetingPoint is { } meeting)
        {
            checks.Add((meeting, impact.StartAtUtc, impact.StartAtUtc));
        }

        foreach (var stop in impact.RequiredStops)
        {
            if (stop.Required == false)
            {
                continue;
            }

            if (stop.PlannedVisitAtUtc is { } visitUtc)
            {
                DateTimeOffset departUtc = stop.PlannedDepartureAtUtc is { } dep && dep >= visitUtc
                    ? dep
                    : visitUtc;
                checks.Add((stop.Point, visitUtc, departUtc));
            }
            else
            {
                checks.Add((stop.Point, impact.StartAtUtc, impact.EndAtUtc));
            }
        }

        if (checks.Count == 0)
        {
            return ForecastCheckOutcome.Inconclusive;
        }

        bool anyCheckMissingCoverage = false;
        foreach (var (point, windowStart, windowEnd) in checks)
        {
            var cell = point.MonitoringCell;
            if (!forecastCache.TryGetValue(cell, out var forecast))
            {
                forecast = await alertsClient.GetHourlyForecastAsync(
                    (double)cell.Latitude,
                    (double)cell.Longitude,
                    cancellationToken);
                forecastCache[cell] = forecast;
            }

            if (forecast.Outcome is TomorrowCallOutcome.Unavailable or TomorrowCallOutcome.NotAuthorized)
            {
                return ForecastCheckOutcome.ProviderAborted;
            }

            if (forecast.Outcome != TomorrowCallOutcome.Success || forecast.Hours.Count == 0)
            {
                return ForecastCheckOutcome.Inconclusive;
            }

            int checkMatchedSlots = 0;
            foreach (var slot in forecast.Hours)
            {
                if (!SlotCoversWindow(slot.TimeUtc, windowStart, windowEnd))
                {
                    continue;
                }

                checkMatchedSlots++;
                if (IsHazardous(slot, opts))
                {
                    return ForecastCheckOutcome.Hazardous;
                }
            }

            if (checkMatchedSlots == 0)
            {
                anyCheckMissingCoverage = true;
            }
        }

        return anyCheckMissingCoverage ? ForecastCheckOutcome.Inconclusive : ForecastCheckOutcome.Clear;
    }

    private static DateTimeOffset? ResolveDueCheckpointUtc(
        DateTimeOffset startAtUtc,
        int[] descendingCheckpointHours,
        DateTimeOffset nowUtc)
    {
        DateTimeOffset? latestDue = null;
        foreach (int hoursBefore in descendingCheckpointHours)
        {
            DateTimeOffset checkpointUtc = startAtUtc.AddHours(-hoursBefore);
            if (checkpointUtc <= nowUtc)
            {
                latestDue = checkpointUtc;
            }
        }

        return latestDue;
    }

    private static bool SlotCoversWindow(
        DateTimeOffset slotStartUtc,
        DateTimeOffset windowStartUtc,
        DateTimeOffset windowEndUtc)
    {
        DateTimeOffset slotEndUtc = slotStartUtc.AddHours(1);
        return windowEndUtc > windowStartUtc
            ? slotStartUtc < windowEndUtc && slotEndUtc > windowStartUtc
            : slotStartUtc <= windowStartUtc && windowStartUtc < slotEndUtc;
    }

    internal static bool IsHazardous(TomorrowHourlyForecastSlot slot, WeatherAlertProcessingOptions opts) =>
        slot.RainIntensityMmPerHour > opts.SevereRainMmPerHour
        || slot.WindSpeedMetersPerSecond > opts.SevereWindSpeedMetersPerSecond
        || slot.WindGustMetersPerSecond > opts.SevereWindGustMetersPerSecond
        || (double.IsFinite(slot.VisibilityKm) && slot.VisibilityKm < opts.LowVisibilityKm)
        || (opts.ThunderstormWeatherCode > 0 && slot.WeatherCode == opts.ThunderstormWeatherCode);
}