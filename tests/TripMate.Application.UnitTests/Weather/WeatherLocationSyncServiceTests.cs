using FluentAssertions;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Features.Weather.Locations;
using TripMate.Application.UnitTests.TestUtilities;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Application.UnitTests.Weather;

public sealed class WeatherLocationSyncServiceTests
{
    private sealed class TestTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }

    private sealed class StubTomorrowAlertsClient : ITomorrowAlertsClient
    {
        public List<(string Name, double Lat, double Lon)> CreatedLocations { get; } = [];
        public List<string> DeletedLocationIds { get; } = [];
        public bool ReturnNoLocationId { get; set; }

        public Task<TomorrowNotificationsResponse> GetNotificationsAsync(DateTimeOffset startTime, DateTimeOffset endTime, string? pageToken, CancellationToken cancellationToken = default)
            => Task.FromResult(new TomorrowNotificationsResponse([], null));

        public Task<string> CreateLocationAsync(string name, double latitude, double longitude, CancellationToken cancellationToken = default)
        {
            CreatedLocations.Add((name, latitude, longitude));
            return Task.FromResult(ReturnNoLocationId ? string.Empty : $"tomorrow-loc-{CreatedLocations.Count}");
        }

        public Task DeleteLocationAsync(string locationId, CancellationToken cancellationToken = default)
        {
            DeletedLocationIds.Add(locationId);
            return Task.CompletedTask;
        }
    }

    private readonly TestTimeProvider _timeProvider = new(new DateTimeOffset(2026, 11, 2, 12, 0, 0, TimeSpan.Zero));

    private static Tour CreateTestTour(long id, string title)
    {
        var tour = (Tour)Activator.CreateInstance(typeof(Tour), nonPublic: true)!;
        typeof(Tour).GetProperty(nameof(Tour.Id))!.SetValue(tour, id);
        typeof(Tour).GetProperty(nameof(Tour.Title))!.SetValue(tour, title);
        typeof(Tour).GetProperty(nameof(Tour.Status))!.SetValue(tour, TourStatus.Published);
        return tour;
    }

    private static PointOfInterest CreateTestPoi(long id, string name, decimal lat, decimal lon)
    {
        var category = PoiCategory.Create("Landmark", null);
        var poi = PointOfInterest.Create(category, name, lat, lon, 1, new DateTimeOffset(2026, 11, 2, 12, 0, 0, TimeSpan.Zero), "Address", "Desc");
        typeof(PointOfInterest).GetProperty(nameof(PointOfInterest.Id))!.SetValue(poi, id);
        return poi;
    }

    [Fact]
    public async Task SyncUpcomingLocationsAsync_MultipleDeparturesSharingPoi_CreatesOnlyOneMapping()
    {
        using var context = TestDbContext.Create(Guid.NewGuid().ToString());
        var client = new StubTomorrowAlertsClient();
        var service = new WeatherLocationSyncService(context, client, _timeProvider, NullLogger<WeatherLocationSyncService>.Instance);

        var poi = CreateTestPoi(100, "Ba Na Hills", 15.9988m, 108.0012m);
        var tour = CreateTestTour(1, "Da Nang Highlights");

        var item = TourItineraryItem.Create(
            dayNo: 1, sequenceNo: 1, poiId: poi.Id, plannedTime: null, stayDurationMinutes: 120, notes: "Visit", isRequiredForWeather: true);
        typeof(TourItineraryItem).GetProperty(nameof(TourItineraryItem.TourId))!.SetValue(item, tour.Id);
        typeof(TourItineraryItem).GetProperty(nameof(TourItineraryItem.Tour))!.SetValue(item, tour);
        typeof(TourItineraryItem).GetProperty(nameof(TourItineraryItem.Poi))!.SetValue(item, poi);

        var list = (List<TourItineraryItem>)typeof(Tour).GetField("_itineraryItems", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(tour)!;
        list.Add(item);

        var schedule1 = TourSchedule.Create(
            _timeProvider.GetUtcNow().AddDays(1),
            _timeProvider.GetUtcNow().AddDays(1).AddHours(8),
            "City Center", 20, _timeProvider.GetUtcNow());
        typeof(TourSchedule).GetProperty(nameof(TourSchedule.Id))!.SetValue(schedule1, 101L);
        typeof(TourSchedule).GetProperty(nameof(TourSchedule.TourId))!.SetValue(schedule1, tour.Id);
        typeof(TourSchedule).GetProperty(nameof(TourSchedule.Tour))!.SetValue(schedule1, tour);

        var schedule2 = TourSchedule.Create(
            _timeProvider.GetUtcNow().AddDays(2),
            _timeProvider.GetUtcNow().AddDays(2).AddHours(8),
            "City Center", 20, _timeProvider.GetUtcNow());
        typeof(TourSchedule).GetProperty(nameof(TourSchedule.Id))!.SetValue(schedule2, 102L);
        typeof(TourSchedule).GetProperty(nameof(TourSchedule.TourId))!.SetValue(schedule2, tour.Id);
        typeof(TourSchedule).GetProperty(nameof(TourSchedule.Tour))!.SetValue(schedule2, tour);

        context.PointsOfInterest.Add(poi);
        context.Tours.Add(tour);
        context.TourItineraryItems.Add(item);
        context.TourSchedules.AddRange(schedule1, schedule2);
        await context.SaveChangesAsync();

        var result = await service.SyncUpcomingLocationsAsync(CancellationToken.None);

        result.LocationsCreated.Should().Be(1);
        client.CreatedLocations.Should().HaveCount(1);
        client.CreatedLocations[0].Name.Should().Be("Ba Na Hills");

        var mappings = await context.WeatherLocationMappings.ToListAsync();
        mappings.Should().HaveCount(1);
        mappings[0].TripMateLocationKey.Should().Be($"poi-{poi.Id}");
        mappings[0].TomorrowLocationId.Should().Be("tomorrow-loc-1");
    }

    [Fact]
    public async Task SyncUpcomingLocationsAsync_WhenProviderReturnsNoId_RetriesOnTheNextSync()
    {
        using var context = TestDbContext.Create(Guid.NewGuid().ToString());
        var client = new StubTomorrowAlertsClient { ReturnNoLocationId = true };
        var service = new WeatherLocationSyncService(context, client, _timeProvider, NullLogger<WeatherLocationSyncService>.Instance);
        var poi = CreateTestPoi(100, "Ba Na Hills", 15.9988m, 108.0012m);
        var tour = CreateTestTour(1, "Da Nang Highlights");
        var item = TourItineraryItem.Create(
            dayNo: 1, sequenceNo: 1, poiId: poi.Id, plannedTime: null, stayDurationMinutes: 120, notes: "Visit", isRequiredForWeather: true);
        typeof(TourItineraryItem).GetProperty(nameof(TourItineraryItem.TourId))!.SetValue(item, tour.Id);
        typeof(TourItineraryItem).GetProperty(nameof(TourItineraryItem.Tour))!.SetValue(item, tour);
        typeof(TourItineraryItem).GetProperty(nameof(TourItineraryItem.Poi))!.SetValue(item, poi);
        var list = (List<TourItineraryItem>)typeof(Tour).GetField("_itineraryItems", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(tour)!;
        list.Add(item);
        var schedule = TourSchedule.Create(
            _timeProvider.GetUtcNow().AddDays(1),
            _timeProvider.GetUtcNow().AddDays(1).AddHours(8),
            "City Center", 20, _timeProvider.GetUtcNow());
        typeof(TourSchedule).GetProperty(nameof(TourSchedule.Id))!.SetValue(schedule, 101L);
        typeof(TourSchedule).GetProperty(nameof(TourSchedule.TourId))!.SetValue(schedule, tour.Id);
        typeof(TourSchedule).GetProperty(nameof(TourSchedule.Tour))!.SetValue(schedule, tour);
        context.PointsOfInterest.Add(poi);
        context.Tours.Add(tour);
        context.TourItineraryItems.Add(item);
        context.TourSchedules.Add(schedule);
        await context.SaveChangesAsync();

        var refused = await service.SyncUpcomingLocationsAsync(CancellationToken.None);

        refused.LocationsCreated.Should().Be(0);
        (await context.WeatherLocationMappings.CountAsync()).Should().Be(0);

        client.ReturnNoLocationId = false;
        var retried = await service.SyncUpcomingLocationsAsync(CancellationToken.None);

        retried.LocationsCreated.Should().Be(1);
        (await context.WeatherLocationMappings.SingleAsync()).TomorrowLocationId.Should().Be("tomorrow-loc-2");
    }

    [Fact]
    public async Task SyncUpcomingLocationsAsync_DeactivatesExpiredLocationBeyondGracePeriod()
    {
        using var context = TestDbContext.Create(Guid.NewGuid().ToString());
        var client = new StubTomorrowAlertsClient();
        var service = new WeatherLocationSyncService(context, client, _timeProvider, NullLogger<WeatherLocationSyncService>.Instance);

        // Pre-existing mapping expired 2 days ago (beyond 24h grace period)
        var oldMapping = WeatherLocationMapping.Create(
            tripMateLocationKey: "poi-999",
            tomorrowLocationId: "tomorrow-old-999",
            latitude: 16.0m,
            longitude: 108.0m,
            name: "Old Location",
            nowUtc: _timeProvider.GetUtcNow().AddDays(-10));
        oldMapping.LinkDeparture(_timeProvider.GetUtcNow().AddDays(-2), _timeProvider.GetUtcNow().AddDays(-10));
        context.WeatherLocationMappings.Add(oldMapping);
        await context.SaveChangesAsync();

        var result = await service.SyncUpcomingLocationsAsync(CancellationToken.None);

        result.LocationsDeactivated.Should().Be(1);
        client.DeletedLocationIds.Should().Contain("tomorrow-old-999");

        var updated = await context.WeatherLocationMappings.FindAsync(oldMapping.Id);
        updated!.IsActive.Should().BeFalse();
    }

    [Fact]
    public async Task SyncUpcomingLocationsAsync_UpcomingDeparturePreventsUnlink()
    {
        using var context = TestDbContext.Create(Guid.NewGuid().ToString());
        var client = new StubTomorrowAlertsClient();
        var service = new WeatherLocationSyncService(context, client, _timeProvider, NullLogger<WeatherLocationSyncService>.Instance);

        // Mapping with upcoming departure tomorrow
        var activeMapping = WeatherLocationMapping.Create(
            tripMateLocationKey: "poi-777",
            tomorrowLocationId: "tomorrow-active-777",
            latitude: 16.0m,
            longitude: 108.0m,
            name: "Active Future Location",
            nowUtc: _timeProvider.GetUtcNow().AddDays(-1));
        activeMapping.LinkDeparture(_timeProvider.GetUtcNow().AddDays(1), _timeProvider.GetUtcNow());
        context.WeatherLocationMappings.Add(activeMapping);
        await context.SaveChangesAsync();

        var result = await service.SyncUpcomingLocationsAsync(CancellationToken.None);

        result.LocationsDeactivated.Should().Be(0);
        client.DeletedLocationIds.Should().BeEmpty();

        var updated = await context.WeatherLocationMappings.FindAsync(activeMapping.Id);
        updated!.IsActive.Should().BeTrue();
    }
}
