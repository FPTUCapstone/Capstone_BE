using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Features.TripAlerts.Common;
using TripMate.Application.Features.TripAlerts.Weather;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Application.Features.TripAlerts.Scanning;

public sealed class TripAlertScanner(
    IApplicationDbContext dbContext,
    IDateTimeProvider clock,
    IOptions<TripAlertOptions> options,
    ITripWeatherProvider weatherProvider)
{
    private readonly TripAlertOptions options = options.Value;
    private readonly TripAlertPersistence persistence = new(dbContext);
    private readonly WeatherAlertPolicy weatherPolicy = new(options);

    public async Task ScanAsync(CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;
        await ResolveProgressAndEndedSessionIncidents(now, cancellationToken);

        var sessions = await dbContext.TripSessions
            .Include(session => session.Items)
            .Include(session => session.Itinerary)
                .ThenInclude(itinerary => itinerary.SchedulingRequest)
            .Include(session => session.Itinerary)
                .ThenInclude(itinerary => itinerary.SourceTour)
            .Where(session =>
                session.EndedAtUtc == null
                && (session.ExpiresAtUtc == null || session.ExpiresAtUtc > now)
                && (session.FsmState == TripSession.NavigatingState
                    || session.FsmState == TripSession.ExploringState))
            .ToArrayAsync(cancellationToken);

        foreach (var session in sessions)
        {
            var pendingItems = session.Items
                .Where(item => item.Status == TripSessionItem.PendingStatus)
                .OrderBy(item => item.SequenceNo)
                .ToArray();
            if (pendingItems.Length == 0)
            {
                continue;
            }

            await ScanDelay(session, pendingItems[0], now, cancellationToken);
            await ScanPoisAndWeather(session, pendingItems, now, cancellationToken);
        }
    }

    private async Task ResolveProgressAndEndedSessionIncidents(
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var incidents = await dbContext.Incidents
            .Include(incident => incident.Session)
                .ThenInclude(session => session.Items)
            .Where(incident => incident.ResolvedAtUtc == null && incident.SourceKey != null)
            .ToArrayAsync(cancellationToken);
        var changed = false;
        foreach (var incident in incidents)
        {
            var sessionEnded = incident.Session.EndedAtUtc.HasValue
                || incident.Session.FsmState == TripSession.CompletedState;
            var item = incident.AffectedItineraryItemId.HasValue
                ? incident.Session.Items.SingleOrDefault(candidate =>
                    candidate.ItineraryItemId == incident.AffectedItineraryItemId.Value)
                : null;
            var reached = item?.ReachedAtUtc.HasValue == true;
            var skipped = item?.SkippedAtUtc.HasValue == true
                && (incident.IncidentType != Incident.RouteDeviationType
                    || item.SkippedAtUtc >= incident.DetectedAtUtc);
            var progressResolved = reached || skipped;
            if (sessionEnded || progressResolved)
            {
                changed |= incident.Resolve(now);
            }
        }

        if (changed)
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
    }

    private Task ScanDelay(
        TripSession session,
        TripSessionItem next,
        DateTimeOffset now,
        CancellationToken cancellationToken) =>
        now > next.PlannedArrivalUtc + options.ScheduleDelayThreshold
            ? EnsurePersisted(
                session,
                Incident.ScheduleDelayType,
                $"delay:{next.ItineraryItemId}",
                next.ItineraryItemId,
                "Arrival at this stop has not been recorded on time.",
                now,
                cancellationToken)
            : ResolveBySource(session.Id, Incident.ScheduleDelayType,
                $"delay:{next.ItineraryItemId}", now, cancellationToken);

    private async Task ScanPoisAndWeather(
        TripSession session,
        IReadOnlyCollection<TripSessionItem> items,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var poiIds = items.Select(item => item.PoiId).Distinct().ToArray();
        var pois = await dbContext.PointsOfInterest
            .Include(poi => poi.OpeningHours)
            .Where(poi => poiIds.Contains(poi.Id))
            .ToDictionaryAsync(poi => poi.Id, cancellationToken);
        var timeZoneId = session.Itinerary.SchedulingRequest?.TimeZoneId
            ?? session.Itinerary.SourceTour?.TimeZoneId;

        foreach (var item in items)
        {
            if (!pois.TryGetValue(item.PoiId, out var poi))
            {
                continue;
            }

            var closed = poi.Status == PointOfInterestStatus.Inactive
                || IsClosedAt(poi, item.PlannedArrivalUtc, timeZoneId);
            var closureKey = $"closure:{item.ItineraryItemId}";
            if (closed)
            {
                await EnsurePersisted(
                    session,
                    Incident.PoiClosureType,
                    closureKey,
                    item.ItineraryItemId,
                    "The point of interest is unavailable at the planned visit time.",
                    now,
                    cancellationToken);
            }
            else
            {
                await ResolveBySource(
                    session.Id,
                    Incident.PoiClosureType,
                    closureKey,
                    now,
                    cancellationToken);
            }

            var untilVisit = item.PlannedArrivalUtc - now;
            if (untilVisit > TimeSpan.FromHours(3)
                || (untilVisit < TimeSpan.Zero && item != items.OrderBy(i => i.SequenceNo).First()))
            {
                continue;
            }

            TripWeatherSnapshot? weather;
            try
            {
                weather = await weatherProvider.GetAsync(
                    new WeatherForecastRequest(item.Latitude, item.Longitude,
                        untilVisit < TimeSpan.Zero ? now : item.PlannedArrivalUtc,
                        UseRealtime: untilVisit < TimeSpan.Zero),
                    cancellationToken);
            }
            catch (Exception) when (!cancellationToken.IsCancellationRequested)
            {
                continue;
            }

            if (weather is null)
            {
                continue;
            }

            await ScanWeather(session, item, poi.IndoorOutdoor,
                poi.IndoorOutdoorReviewedAtUtc.HasValue, weather, now, cancellationToken);
        }
    }

    private async Task ScanWeather(
        TripSession session,
        TripSessionItem item,
        IndoorOutdoorType indoorOutdoor,
        bool classificationReviewed,
        TripWeatherSnapshot weather,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var activeKeys = new HashSet<string>(StringComparer.Ordinal);
        var action = weatherPolicy.Decide(weather.RainMmPerHour, weather.Thunderstorm,
            indoorOutdoor, classificationReviewed, item.PlannedArrivalUtc - now, weather.Basis);
        if (action == WeatherAlertAction.PersistedAdvisory)
        {
            var key = $"{WeatherAlertPolicy.ModerateRainSourcePrefix}{item.ItineraryItemId}:{item.PlannedArrivalUtc:O}";
            var alreadyActive = await dbContext.Incidents.AnyAsync(incident =>
                incident.SessionId == session.Id
                && incident.IncidentType == Incident.WeatherAdvisoryType
                && incident.SourceKey == key
                && incident.ResolvedAtUtc == null, cancellationToken);
            var cooldown = await dbContext.Incidents.AnyAsync(incident =>
                incident.SessionId == session.Id
                && incident.IncidentType == Incident.WeatherAdvisoryType
                && incident.DetectedAtUtc > now.AddHours(-3), cancellationToken);
            if (alreadyActive || !cooldown)
            {
                activeKeys.Add(key);
                await EnsurePersisted(session, Incident.WeatherAdvisoryType, key,
                    item.ItineraryItemId, null, now, cancellationToken, weather);
            }
        }
        else if (action == WeatherAlertAction.Warning)
        {
            var prefix = weather.Thunderstorm ? WeatherAlertPolicy.ThunderstormSourcePrefix
                : weather.RainMmPerHour >= options.ExtremeRainMmPerHour
                    ? WeatherAlertPolicy.ExtremeRainSourcePrefix
                    : WeatherAlertPolicy.HeavyRainSourcePrefix;
            var key = $"{prefix}{item.ItineraryItemId}:{item.PlannedArrivalUtc:O}";
            activeKeys.Add(key);
            await EnsurePersisted(session, Incident.SevereWeatherType, key,
                item.ItineraryItemId, null, now, cancellationToken, weather);
        }

        var stale = await dbContext.Incidents
            .Where(incident =>
                incident.SessionId == session.Id
                && (incident.IncidentType == Incident.SevereWeatherType
                    || incident.IncidentType == Incident.WeatherAdvisoryType)
                && incident.AffectedItineraryItemId == item.ItineraryItemId
                && incident.ResolvedAtUtc == null
                && incident.SourceKey != null)
            .ToArrayAsync(cancellationToken);
        var changed = false;
        foreach (var incident in stale.Where(incident => !activeKeys.Contains(incident.SourceKey!)))
        {
            changed |= incident.Resolve(now);
        }

        if (changed)
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
    }

    private async Task EnsurePersisted(
        TripSession session,
        string type,
        string sourceKey,
        long itemId,
        string? description,
        DateTimeOffset detectedAtUtc,
        CancellationToken cancellationToken,
        TripWeatherSnapshot? weather = null)
    {
        if (await dbContext.Incidents.AnyAsync(incident =>
                incident.SessionId == session.Id
                && incident.IncidentType == type
                && incident.SourceKey == sourceKey
                && incident.ResolvedAtUtc == null,
                cancellationToken))
        {
            return;
        }

        try
        {
            await dbContext.ExecuteInTransactionAsync(async ct =>
            {
                if (await dbContext.Incidents.AnyAsync(incident =>
                        incident.SessionId == session.Id
                        && incident.IncidentType == type
                        && incident.SourceKey == sourceKey
                        && incident.ResolvedAtUtc == null,
                        ct))
                {
                    return false;
                }

                await persistence.CreateAsync(
                    session, type, sourceKey, detectedAtUtc, itemId, null, description, ct, weather);
                return true;
            }, cancellationToken);
        }
        catch (DbUpdateException)
        {
            dbContext.ClearTrackedEntities();
            if (!await dbContext.Incidents.AsNoTracking().AnyAsync(incident =>
                    incident.SessionId == session.Id
                    && incident.IncidentType == type
                    && incident.SourceKey == sourceKey
                    && incident.ResolvedAtUtc == null,
                    cancellationToken))
            {
                throw;
            }
        }
    }

    private async Task ResolveBySource(
        long sessionId,
        string type,
        string sourceKey,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var incident = await dbContext.Incidents.SingleOrDefaultAsync(candidate =>
            candidate.SessionId == sessionId
            && candidate.IncidentType == type
            && candidate.SourceKey == sourceKey
            && candidate.ResolvedAtUtc == null,
            cancellationToken);
        if (incident?.Resolve(now) == true)
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
    }

    private static bool IsClosedAt(
        PointOfInterest poi,
        DateTimeOffset plannedAtUtc,
        string? timeZoneId)
    {
        if (string.IsNullOrWhiteSpace(timeZoneId)
            || !TimeZoneInfo.TryFindSystemTimeZoneById(timeZoneId, out var timeZone))
        {
            return false;
        }

        var local = TimeZoneInfo.ConvertTime(plannedAtUtc, timeZone);
        var hours = poi.OpeningHours.SingleOrDefault(candidate =>
            candidate.DayOfWeek == (byte)local.DayOfWeek);
        if (hours is null)
        {
            return false;
        }

        return hours.IsClosed
            || local.TimeOfDay < hours.OpenTime!.Value.ToTimeSpan()
            || local.TimeOfDay >= hours.CloseTime!.Value.ToTimeSpan();
    }
}