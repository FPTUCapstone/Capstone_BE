using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Features.Weather.Emergency;
using TripMate.Application.Features.Weather.Processing;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Application.Features.Weather.Locations;

/// <summary>
/// Keeps Tomorrow.io monitoring exactly the points UC-70 needs: the meeting point and the
/// weather-required stops of every departure that has not ended within the lookahead window.
/// A Location only produces notifications once it is linked to the configured Alert, so every
/// cycle re-checks the links. A refused credential or an unavailable provider ends the cycle's
/// remote calls; the next cycle retries, which bounds traffic to one attempt per interval.
/// </summary>
public sealed class WeatherLocationSyncService(
    IApplicationDbContext context,
    ITomorrowAlertsClient alertsClient,
    IOptions<WeatherAlertProcessingOptions> options,
    TimeProvider timeProvider,
    ILogger<WeatherLocationSyncService> logger) : IWeatherLocationSyncService
{
    private static readonly TimeSpan GracePeriod = TimeSpan.FromHours(24);

    public async Task<WeatherLocationSyncResult> SyncUpcomingLocationsAsync(CancellationToken cancellationToken)
    {
        var nowUtc = timeProvider.GetUtcNow();
        var required = await LoadRequiredPointsAsync(nowUtc, cancellationToken);

        var requiredKeys = required.Keys.ToList();
        var mappings = await context.WeatherLocationMappings
            .Where(mapping => mapping.IsActive || requiredKeys.Contains(mapping.TripMateLocationKey))
            .ToListAsync(cancellationToken);
        var byKey = mappings.ToDictionary(mapping => mapping.TripMateLocationKey, StringComparer.OrdinalIgnoreCase);

        bool providerAvailable = true;
        bool creating = true;
        int created = 0;
        int updated = 0;

        foreach (var point in required.Values)
        {
            if (byKey.TryGetValue(point.Key, out var mapping) && mapping.IsActive)
            {
                mapping.ExtendMonitoring(point.LastDepartureEndUtc, nowUtc);
                updated++;
                continue;
            }

            if (!creating)
            {
                continue;
            }

            var location = await alertsClient.CreateLocationAsync(
                point.Name, (double)point.Latitude, (double)point.Longitude, cancellationToken);
            if (location.Outcome != TomorrowCallOutcome.Success || string.IsNullOrWhiteSpace(location.LocationId))
            {
                LogRefusal("create a Location", location.Outcome);
                creating = false;
                providerAvailable = !IsProviderDown(location.Outcome);
                continue;
            }

            if (mapping is null)
            {
                mapping = WeatherLocationMapping.Create(point.Key, location.LocationId, point.Latitude,
                    point.Longitude, point.Name, point.LastDepartureEndUtc, nowUtc);
                context.WeatherLocationMappings.Add(mapping);
                byKey[point.Key] = mapping;
            }
            else
            {
                mapping.Reactivate(location.LocationId, point.LastDepartureEndUtc, nowUtc);
            }

            created++;
        }

        // A created Location is saved before linking so a failed link never creates it twice.
        await context.SaveChangesAsync(cancellationToken);

        int linked = 0;
        if (providerAvailable)
        {
            (var outcome, linked) = await LinkMissingAsync(required, byKey, cancellationToken);
            // A local link failure (404, bad body) must not block retiring expired Locations.
            providerAvailable = !IsProviderDown(outcome);
        }

        int deactivated = providerAvailable ? await RetireExpiredAsync(required, byKey, nowUtc, cancellationToken) : 0;
        await context.SaveChangesAsync(cancellationToken);

        logger.LogInformation(
            "Weather location sync: {Created} created, {Updated} kept, {Linked} linked, {Deactivated} retired.",
            created, updated, linked, deactivated);
        return new WeatherLocationSyncResult(created, updated, deactivated, linked);
    }

    private async Task<(TomorrowCallOutcome Outcome, int Linked)> LinkMissingAsync(
        IReadOnlyDictionary<string, RequiredPoint> required,
        IReadOnlyDictionary<string, WeatherLocationMapping> byKey,
        CancellationToken cancellationToken)
    {
        var monitored = required.Keys
            .Select(key => byKey.TryGetValue(key, out var mapping) && mapping.IsActive ? mapping.TomorrowLocationId : null)
            .OfType<string>()
            .ToList();
        if (monitored.Count == 0)
        {
            return (TomorrowCallOutcome.Success, 0);
        }

        var linked = await alertsClient.GetLinkedLocationIdsAsync(cancellationToken);
        if (linked.Outcome != TomorrowCallOutcome.Success)
        {
            LogRefusal("read the Alert's linked Locations", linked.Outcome);
            return (linked.Outcome, 0);
        }

        var missing = monitored.Where(id => !linked.LocationIds.Contains(id)).ToList();
        if (missing.Count == 0)
        {
            return (TomorrowCallOutcome.Success, 0);
        }

        var outcome = await alertsClient.LinkLocationsAsync(missing, cancellationToken);
        if (outcome != TomorrowCallOutcome.Success)
        {
            LogRefusal("link Locations to the Alert", outcome);
            return (outcome, 0);
        }

        return (outcome, missing.Count);
    }

    private async Task<int> RetireExpiredAsync(
        IReadOnlyDictionary<string, RequiredPoint> required,
        IReadOnlyDictionary<string, WeatherLocationMapping> byKey,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken)
    {
        var cutoffUtc = nowUtc - GracePeriod;
        var expired = byKey.Values
            .Where(mapping => mapping.IsActive
                && !required.ContainsKey(mapping.TripMateLocationKey)
                && mapping.LastDepartureEndAtUtc < cutoffUtc)
            .ToList();
        if (expired.Count == 0)
        {
            return 0;
        }

        // Unlink first so the Alert never points at a deleted Location. 404 means already gone.
        var unlink = await alertsClient.UnlinkLocationsAsync(
            expired.Select(mapping => mapping.TomorrowLocationId).ToList(), cancellationToken);
        if (unlink is not (TomorrowCallOutcome.Success or TomorrowCallOutcome.NotFound))
        {
            LogRefusal("unlink expired Locations", unlink);
            return 0;
        }

        int deactivated = 0;
        foreach (var mapping in expired)
        {
            var delete = await alertsClient.DeleteLocationAsync(mapping.TomorrowLocationId, cancellationToken);
            if (delete is not (TomorrowCallOutcome.Success or TomorrowCallOutcome.NotFound))
            {
                // Kept active: the next cycle unlinks (idempotent) and deletes it again.
                LogRefusal("delete an expired Location", delete);
                break;
            }

            mapping.Deactivate(nowUtc);
            deactivated++;
        }

        return deactivated;
    }

    private async Task<Dictionary<string, RequiredPoint>> LoadRequiredPointsAsync(
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken)
    {
        var lookaheadEndUtc = nowUtc.AddDays(Math.Max(1, options.Value.LocationMonitoringLookaheadDays));

        // Departures still running stay monitored: an active tour is exactly when a stop decision matters.
        var schedules = await context.TourSchedules
            .AsNoTracking()
            .Include(s => s.Tour)
                .ThenInclude(t => t.ItineraryItems)
                    .ThenInclude(i => i.Poi)
            .Where(s => s.Status == TourScheduleStatus.Scheduled
                && s.EndAtUtc > nowUtc
                && s.StartAtUtc <= lookaheadEndUtc)
            .ToListAsync(cancellationToken);

        var required = new Dictionary<string, RequiredPoint>(StringComparer.OrdinalIgnoreCase);
        void Need(string key, string name, decimal latitude, decimal longitude, DateTimeOffset endUtc)
        {
            if (!required.TryGetValue(key, out var existing) || endUtc > existing.LastDepartureEndUtc)
            {
                required[key] = new RequiredPoint(key, name, latitude, longitude, endUtc);
            }
        }

        foreach (var schedule in schedules)
        {
            if (schedule.MeetingLatitude is { } latitude && schedule.MeetingLongitude is { } longitude)
            {
                // Meeting points share a Location per cell; the processor matches on the same cell.
                var cell = new WeatherPoint(latitude, longitude).MonitoringCell;
                Need($"meeting-{cell.Latitude:F4}_{cell.Longitude:F4}", $"Meeting Point {cell.Latitude:F4},{cell.Longitude:F4}",
                    latitude, longitude, schedule.EndAtUtc);
            }

            // Only stops the Operator marked as weather-critical are sent to the provider.
            foreach (var item in schedule.Tour.ItineraryItems.Where(item => item.IsRequiredForWeather == true && item.Poi is not null))
            {
                Need($"poi-{item.Poi.Id}", item.Poi.Name ?? $"POI {item.Poi.Id}",
                    item.Poi.Latitude, item.Poi.Longitude, schedule.EndAtUtc);
            }
        }

        return required;
    }

    // 401/403 or 429/5xx/network: nothing else this cycle can succeed.
    private static bool IsProviderDown(TomorrowCallOutcome outcome) =>
        outcome is TomorrowCallOutcome.NotAuthorized or TomorrowCallOutcome.Unavailable;

    private void LogRefusal(string action, TomorrowCallOutcome outcome)
    {
        if (outcome == TomorrowCallOutcome.NotAuthorized)
        {
            logger.LogError("Tomorrow.io refused to {Action}: check the API key, AlertId and plan entitlement.", action);
        }
        else
        {
            logger.LogWarning("Tomorrow.io could not {Action} ({Outcome}); retrying on the next sync.", action, outcome);
        }
    }

    private sealed record RequiredPoint(
        string Key,
        string Name,
        decimal Latitude,
        decimal Longitude,
        DateTimeOffset LastDepartureEndUtc);
}