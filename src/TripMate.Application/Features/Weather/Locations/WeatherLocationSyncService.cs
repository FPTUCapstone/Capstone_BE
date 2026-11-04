using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

using TripMate.Application.Common.Interfaces;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Application.Features.Weather.Locations;

public sealed class WeatherLocationSyncService(
    IApplicationDbContext context,
    ITomorrowAlertsClient alertsClient,
    TimeProvider timeProvider,
    ILogger<WeatherLocationSyncService> logger) : IWeatherLocationSyncService
{
    private const int LookaheadDays = 7;
    private static readonly TimeSpan GracePeriod = TimeSpan.FromHours(24);

    public async Task<WeatherLocationSyncResult> SyncUpcomingLocationsAsync(CancellationToken cancellationToken)
    {
        var nowUtc = timeProvider.GetUtcNow();
        var lookaheadEndUtc = nowUtc.AddDays(LookaheadDays);

        var upcomingSchedules = await context.TourSchedules
            .AsNoTracking()
            .Include(s => s.Tour)
                .ThenInclude(t => t.ItineraryItems)
                    .ThenInclude(i => i.Poi)
            .Where(s => s.Status == TourScheduleStatus.Scheduled &&
                        s.StartAtUtc >= nowUtc &&
                        s.StartAtUtc <= lookaheadEndUtc)
            .ToListAsync(cancellationToken);

        var requiredLocations = new Dictionary<string, (string Key, string Name, decimal Lat, decimal Lon, DateTimeOffset MaxEndUtc)>(StringComparer.OrdinalIgnoreCase);

        foreach (var schedule in upcomingSchedules)
        {
            if (schedule.MeetingLatitude.HasValue && schedule.MeetingLongitude.HasValue)
            {
                var key = $"meeting-{schedule.MeetingLatitude.Value:F4}_{schedule.MeetingLongitude.Value:F4}";
                var name = $"Meeting Point {schedule.MeetingLatitude.Value:F4},{schedule.MeetingLongitude.Value:F4}";
                if (!requiredLocations.TryGetValue(key, out var existing) || schedule.EndAtUtc > existing.MaxEndUtc)
                {
                    requiredLocations[key] = (key, name, schedule.MeetingLatitude.Value, schedule.MeetingLongitude.Value, schedule.EndAtUtc);
                }
            }

            if (schedule.Tour?.ItineraryItems is not null)
            {
                foreach (var item in schedule.Tour.ItineraryItems)
                {
                    if (item.Poi is not null)
                    {
                        var key = $"poi-{item.Poi.Id}";
                        var name = item.Poi.Name ?? $"POI {item.Poi.Id}";
                        if (!requiredLocations.TryGetValue(key, out var existing) || schedule.EndAtUtc > existing.MaxEndUtc)
                        {
                            requiredLocations[key] = (key, name, item.Poi.Latitude, item.Poi.Longitude, schedule.EndAtUtc);
                        }
                    }
                }
            }
        }

        var existingKeys = requiredLocations.Keys.ToList();
        var existingMappings = await context.WeatherLocationMappings
            .Where(m => existingKeys.Contains(m.TripMateLocationKey))
            .ToListAsync(cancellationToken);

        var existingByKey = existingMappings.ToDictionary(m => m.TripMateLocationKey, StringComparer.OrdinalIgnoreCase);

        int created = 0;
        int updated = 0;

        foreach (var (key, loc) in requiredLocations)
        {
            if (existingByKey.TryGetValue(key, out var mapping))
            {
                mapping.LinkDeparture(loc.MaxEndUtc, nowUtc);
                updated++;
            }
            else
            {
                var tomorrowLocId = await alertsClient.CreateLocationAsync(loc.Name, (double)loc.Lat, (double)loc.Lon, cancellationToken);
                if (string.IsNullOrWhiteSpace(tomorrowLocId))
                {
                    logger.LogWarning("Weather provider returned no location id for {LocationKey}; retrying on the next sync.", loc.Key);
                    continue;
                }

                var newMapping = WeatherLocationMapping.Create(
                    tripMateLocationKey: loc.Key,
                    tomorrowLocationId: tomorrowLocId,
                    latitude: loc.Lat,
                    longitude: loc.Lon,
                    name: loc.Name,
                    nowUtc: nowUtc);

                newMapping.LinkDeparture(loc.MaxEndUtc, nowUtc);
                context.WeatherLocationMappings.Add(newMapping);
                created++;
            }
        }

        // Cleanup expired locations (past last departure + grace period and not needed)
        var graceCutoffUtc = nowUtc.Subtract(GracePeriod);
        var activeMappings = await context.WeatherLocationMappings
            .Where(m => m.IsActive)
            .ToListAsync(cancellationToken);

        int deactivated = 0;
        foreach (var mapping in activeMappings)
        {
            if (requiredLocations.ContainsKey(mapping.TripMateLocationKey))
            {
                continue;
            }

            if (mapping.LastDepartureEndAtUtc.HasValue && mapping.LastDepartureEndAtUtc.Value < graceCutoffUtc)
            {
                await alertsClient.DeleteLocationAsync(mapping.TomorrowLocationId, cancellationToken);

                mapping.Deactivate(nowUtc);
                deactivated++;
            }
        }

        if (created > 0 || updated > 0 || deactivated > 0)
        {
            await context.SaveChangesAsync(cancellationToken);
            logger.LogInformation("Weather location sync complete: {Created} created, {Updated} updated, {Deactivated} deactivated.",
                created, updated, deactivated);
        }

        return new WeatherLocationSyncResult(created, updated, deactivated);
    }
}
