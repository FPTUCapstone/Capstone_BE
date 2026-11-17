using FluentAssertions;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Features.Weather.Locations;
using TripMate.Application.Features.Weather.Processing;
using TripMate.Application.UnitTests.TestUtilities;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Application.UnitTests.Weather;

public sealed class WeatherLocationSyncServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 2, 12, 0, 0, TimeSpan.Zero);

    private sealed class TestTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }

    /// <summary>Records every call in order and answers with the outcome each test sets.</summary>
    private sealed class FakeAlertsClient : ITomorrowAlertsClient
    {
        public List<string> Calls { get; } = [];
        public HashSet<string> Linked { get; } = new(StringComparer.Ordinal);
        public TomorrowCallOutcome CreateOutcome { get; set; } = TomorrowCallOutcome.Success;
        public TomorrowCallOutcome ListOutcome { get; set; } = TomorrowCallOutcome.Success;
        public TomorrowCallOutcome LinkOutcome { get; set; } = TomorrowCallOutcome.Success;
        public TomorrowCallOutcome UnlinkOutcome { get; set; } = TomorrowCallOutcome.Success;
        public TomorrowCallOutcome DeleteOutcome { get; set; } = TomorrowCallOutcome.Success;
        private int _created;

        public Task<TomorrowNotificationsResponse> GetNotificationsAsync(DateTimeOffset startTime, DateTimeOffset endTime, string? pageToken, CancellationToken cancellationToken = default)
            => Task.FromResult(new TomorrowNotificationsResponse([], null));

        public Task<TomorrowCreatedLocation> CreateLocationAsync(string name, double latitude, double longitude, CancellationToken cancellationToken = default)
        {
            Calls.Add($"create:{name}");
            return Task.FromResult(CreateOutcome == TomorrowCallOutcome.Success
                ? new TomorrowCreatedLocation(CreateOutcome, $"loc-{++_created}")
                : new TomorrowCreatedLocation(CreateOutcome, null));
        }

        public Task<TomorrowCallOutcome> DeleteLocationAsync(string locationId, CancellationToken cancellationToken = default)
        {
            Calls.Add($"delete:{locationId}");
            return Task.FromResult(DeleteOutcome);
        }

        public Task<TomorrowLinkedLocations> GetLinkedLocationIdsAsync(CancellationToken cancellationToken = default)
        {
            Calls.Add("list-linked");
            return Task.FromResult(new TomorrowLinkedLocations(ListOutcome, Linked.ToHashSet()));
        }

        public Task<TomorrowCallOutcome> LinkLocationsAsync(IReadOnlyCollection<string> locationIds, CancellationToken cancellationToken = default)
        {
            Calls.Add($"link:{string.Join(',', locationIds)}");
            if (LinkOutcome == TomorrowCallOutcome.Success) Linked.UnionWith(locationIds);
            return Task.FromResult(LinkOutcome);
        }

        public Task<TomorrowCallOutcome> UnlinkLocationsAsync(IReadOnlyCollection<string> locationIds, CancellationToken cancellationToken = default)
        {
            Calls.Add($"unlink:{string.Join(',', locationIds)}");
            if (UnlinkOutcome == TomorrowCallOutcome.Success) Linked.ExceptWith(locationIds);
            return Task.FromResult(UnlinkOutcome);
        }
    }

    private static WeatherLocationSyncService Service(TestDbContext context, FakeAlertsClient client, int lookaheadDays = 7) =>
        new(context, client,
            Options.Create(new WeatherAlertProcessingOptions { LocationMonitoringLookaheadDays = lookaheadDays }),
            new TestTimeProvider(Now), NullLogger<WeatherLocationSyncService>.Instance);

    [Fact]
    public async Task NewRequiredStop_IsCreatedThenLinkedToTheAlert()
    {
        using var context = TestDbContext.Create(Guid.NewGuid().ToString());
        SeedDeparture(context, start: Now.AddDays(1), requiredForWeather: true);
        await context.SaveChangesAsync();
        var client = new FakeAlertsClient();

        var result = await Service(context, client).SyncUpcomingLocationsAsync(CancellationToken.None);

        client.Calls.Should().Equal("create:Ba Na Hills", "list-linked", "link:loc-1");
        result.LocationsCreated.Should().Be(1);
        result.LocationsLinked.Should().Be(1);
        (await context.WeatherLocationMappings.SingleAsync()).TomorrowLocationId.Should().Be("loc-1");
    }

    [Fact]
    public async Task MeetingPointAndRequiredStop_AreSynced_ButStopsNotMarkedRequiredAreNot()
    {
        using var context = TestDbContext.Create(Guid.NewGuid().ToString());
        var schedule = SeedDeparture(context, start: Now.AddDays(1), requiredForWeather: true);
        schedule.SetVerifiedMeetingCoordinates(16.054407m, 108.202167m);
        AddStop(context, schedule.Tour, poiId: 200, "Optional Cafe", requiredForWeather: false);
        AddStop(context, schedule.Tour, poiId: 300, "Unclassified Market", requiredForWeather: null);
        await context.SaveChangesAsync();
        var client = new FakeAlertsClient();

        await Service(context, client).SyncUpcomingLocationsAsync(CancellationToken.None);

        client.Calls.Where(call => call.StartsWith("create:", StringComparison.Ordinal)).Should()
            .BeEquivalentTo("create:Meeting Point 16.0544,108.2022", "create:Ba Na Hills");
        (await context.WeatherLocationMappings.Select(m => m.TripMateLocationKey).ToListAsync())
            .Should().BeEquivalentTo("meeting-16.0544_108.2022", "poi-100");
    }

    [Fact]
    public async Task ExistingMappingMissingItsLink_IsRelinkedWithoutCreatingADuplicate()
    {
        using var context = TestDbContext.Create(Guid.NewGuid().ToString());
        SeedDeparture(context, start: Now.AddDays(1), requiredForWeather: true);
        context.WeatherLocationMappings.Add(WeatherLocationMapping.Create(
            "poi-100", "loc-existing", 15.9988m, 108.0012m, "Ba Na Hills", Now.AddDays(1), Now.AddDays(-1)));
        await context.SaveChangesAsync();
        var client = new FakeAlertsClient();

        var result = await Service(context, client).SyncUpcomingLocationsAsync(CancellationToken.None);

        client.Calls.Should().Equal("list-linked", "link:loc-existing");
        result.LocationsCreated.Should().Be(0);
        result.LocationsLinked.Should().Be(1);
        (await context.WeatherLocationMappings.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task AlreadyLinkedLocation_GetsNoExtraLinkCall()
    {
        using var context = TestDbContext.Create(Guid.NewGuid().ToString());
        SeedDeparture(context, start: Now.AddDays(1), requiredForWeather: true);
        context.WeatherLocationMappings.Add(WeatherLocationMapping.Create(
            "poi-100", "loc-existing", 15.9988m, 108.0012m, "Ba Na Hills", Now.AddDays(1), Now.AddDays(-1)));
        await context.SaveChangesAsync();
        var client = new FakeAlertsClient();
        client.Linked.Add("loc-existing");

        var result = await Service(context, client).SyncUpcomingLocationsAsync(CancellationToken.None);

        client.Calls.Should().Equal("list-linked");
        result.LocationsLinked.Should().Be(0);
    }

    [Fact]
    public async Task FailedLink_KeepsTheMapping_AndTheNextCycleLinksWithoutRecreating()
    {
        using var context = TestDbContext.Create(Guid.NewGuid().ToString());
        SeedDeparture(context, start: Now.AddDays(1), requiredForWeather: true);
        await context.SaveChangesAsync();
        var client = new FakeAlertsClient { LinkOutcome = TomorrowCallOutcome.Unavailable };

        var first = await Service(context, client).SyncUpcomingLocationsAsync(CancellationToken.None);
        first.LocationsLinked.Should().Be(0);
        (await context.WeatherLocationMappings.SingleAsync()).TomorrowLocationId.Should().Be("loc-1");

        client.LinkOutcome = TomorrowCallOutcome.Success;
        client.Calls.Clear();
        var second = await Service(context, client).SyncUpcomingLocationsAsync(CancellationToken.None);

        client.Calls.Should().Equal("list-linked", "link:loc-1");
        second.LocationsCreated.Should().Be(0);
        second.LocationsLinked.Should().Be(1);
    }

    [Theory]
    [InlineData(TomorrowCallOutcome.NotAuthorized)]
    [InlineData(TomorrowCallOutcome.NotFound)]
    [InlineData(TomorrowCallOutcome.Unavailable)]
    [InlineData(TomorrowCallOutcome.InvalidResponse)]
    public async Task ProviderFailureOnCreate_StopsTheCycleWithoutARequestStorm(TomorrowCallOutcome outcome)
    {
        using var context = TestDbContext.Create(Guid.NewGuid().ToString());
        var schedule = SeedDeparture(context, start: Now.AddDays(1), requiredForWeather: true);
        AddStop(context, schedule.Tour, poiId: 200, "Hoi An", requiredForWeather: true);
        AddStop(context, schedule.Tour, poiId: 300, "My Son", requiredForWeather: true);
        await context.SaveChangesAsync();
        var client = new FakeAlertsClient { CreateOutcome = outcome };

        var result = await Service(context, client).SyncUpcomingLocationsAsync(CancellationToken.None);

        client.Calls.Should().ContainSingle().Which.Should().StartWith("create:");
        result.LocationsCreated.Should().Be(0);
        (await context.WeatherLocationMappings.CountAsync()).Should().Be(0);
    }

    [Theory]
    [InlineData(TomorrowCallOutcome.NotAuthorized)]
    [InlineData(TomorrowCallOutcome.Unavailable)]
    [InlineData(TomorrowCallOutcome.InvalidResponse)]
    public async Task UnreadableLinkedList_DoesNotLinkBlindly(TomorrowCallOutcome outcome)
    {
        using var context = TestDbContext.Create(Guid.NewGuid().ToString());
        SeedDeparture(context, start: Now.AddDays(1), requiredForWeather: true);
        await context.SaveChangesAsync();
        var client = new FakeAlertsClient { ListOutcome = outcome };

        var result = await Service(context, client).SyncUpcomingLocationsAsync(CancellationToken.None);

        client.Calls.Should().Equal("create:Ba Na Hills", "list-linked");
        result.LocationsLinked.Should().Be(0);
    }

    [Theory]
    [InlineData(TomorrowCallOutcome.NotFound, true)]
    [InlineData(TomorrowCallOutcome.InvalidResponse, true)]
    [InlineData(TomorrowCallOutcome.NotAuthorized, false)]
    [InlineData(TomorrowCallOutcome.Unavailable, false)]
    public async Task FailedLink_BlocksRetirementOnlyWhenTheProviderIsDown(TomorrowCallOutcome outcome, bool retires)
    {
        using var context = TestDbContext.Create(Guid.NewGuid().ToString());
        SeedDeparture(context, start: Now.AddDays(1), requiredForWeather: true);
        context.WeatherLocationMappings.Add(WeatherLocationMapping.Create(
            "poi-999", "loc-old", 16.0m, 108.0m, "Old Location", Now.AddDays(-2), Now.AddDays(-10)));
        await context.SaveChangesAsync();
        var client = new FakeAlertsClient { LinkOutcome = outcome };

        var result = await Service(context, client).SyncUpcomingLocationsAsync(CancellationToken.None);

        result.LocationsDeactivated.Should().Be(retires ? 1 : 0);
        client.Calls.Contains("unlink:loc-old").Should().Be(retires);
    }

    [Fact]
    public async Task ExpiredLocation_IsUnlinkedBeforeItIsDeleted_ThenRetired()
    {
        using var context = TestDbContext.Create(Guid.NewGuid().ToString());
        var expired = WeatherLocationMapping.Create(
            "poi-999", "loc-old", 16.0m, 108.0m, "Old Location", Now.AddDays(-2), Now.AddDays(-10));
        context.WeatherLocationMappings.Add(expired);
        await context.SaveChangesAsync();
        var client = new FakeAlertsClient();

        var result = await Service(context, client).SyncUpcomingLocationsAsync(CancellationToken.None);

        client.Calls.Should().Equal("unlink:loc-old", "delete:loc-old");
        result.LocationsDeactivated.Should().Be(1);
        (await context.WeatherLocationMappings.SingleAsync()).IsActive.Should().BeFalse();
    }

    [Theory]
    [InlineData(TomorrowCallOutcome.NotAuthorized)]
    [InlineData(TomorrowCallOutcome.Unavailable)]
    public async Task FailedUnlink_DeletesNothingAndKeepsTheMappingForRetry(TomorrowCallOutcome outcome)
    {
        using var context = TestDbContext.Create(Guid.NewGuid().ToString());
        context.WeatherLocationMappings.Add(WeatherLocationMapping.Create(
            "poi-999", "loc-old", 16.0m, 108.0m, "Old Location", Now.AddDays(-2), Now.AddDays(-10)));
        await context.SaveChangesAsync();
        var client = new FakeAlertsClient { UnlinkOutcome = outcome };

        var result = await Service(context, client).SyncUpcomingLocationsAsync(CancellationToken.None);

        client.Calls.Should().Equal("unlink:loc-old");
        result.LocationsDeactivated.Should().Be(0);
        (await context.WeatherLocationMappings.SingleAsync()).IsActive.Should().BeTrue();
    }

    [Theory]
    [InlineData(TomorrowCallOutcome.NotAuthorized)]
    [InlineData(TomorrowCallOutcome.Unavailable)]
    public async Task FailedDelete_KeepsTheMappingForRetry(TomorrowCallOutcome outcome)
    {
        using var context = TestDbContext.Create(Guid.NewGuid().ToString());
        context.WeatherLocationMappings.Add(WeatherLocationMapping.Create(
            "poi-999", "loc-old", 16.0m, 108.0m, "Old Location", Now.AddDays(-2), Now.AddDays(-10)));
        await context.SaveChangesAsync();
        var client = new FakeAlertsClient { DeleteOutcome = outcome };

        var result = await Service(context, client).SyncUpcomingLocationsAsync(CancellationToken.None);

        result.LocationsDeactivated.Should().Be(0);
        (await context.WeatherLocationMappings.SingleAsync()).IsActive.Should().BeTrue();
    }

    [Fact]
    public async Task NotFoundOnUnlinkAndDelete_ConfirmsTheLocationIsGone()
    {
        using var context = TestDbContext.Create(Guid.NewGuid().ToString());
        context.WeatherLocationMappings.Add(WeatherLocationMapping.Create(
            "poi-999", "loc-old", 16.0m, 108.0m, "Old Location", Now.AddDays(-2), Now.AddDays(-10)));
        await context.SaveChangesAsync();
        var client = new FakeAlertsClient
        {
            UnlinkOutcome = TomorrowCallOutcome.NotFound,
            DeleteOutcome = TomorrowCallOutcome.NotFound,
        };

        var result = await Service(context, client).SyncUpcomingLocationsAsync(CancellationToken.None);

        result.LocationsDeactivated.Should().Be(1);
        (await context.WeatherLocationMappings.SingleAsync()).IsActive.Should().BeFalse();
    }

    [Fact]
    public async Task LocationStillNeededByAnUpcomingDeparture_IsNotRetired()
    {
        using var context = TestDbContext.Create(Guid.NewGuid().ToString());
        context.WeatherLocationMappings.Add(WeatherLocationMapping.Create(
            "poi-777", "loc-active", 16.0m, 108.0m, "Active", Now.AddDays(1), Now.AddDays(-1)));
        await context.SaveChangesAsync();
        var client = new FakeAlertsClient();

        var result = await Service(context, client).SyncUpcomingLocationsAsync(CancellationToken.None);

        client.Calls.Should().BeEmpty();
        result.LocationsDeactivated.Should().Be(0);
    }

    [Fact]
    public async Task Lookahead_ComesFromOptions()
    {
        using var context = TestDbContext.Create(Guid.NewGuid().ToString());
        SeedDeparture(context, start: Now.AddDays(3), requiredForWeather: true);
        await context.SaveChangesAsync();

        var narrow = new FakeAlertsClient();
        await Service(context, narrow, lookaheadDays: 2).SyncUpcomingLocationsAsync(CancellationToken.None);
        narrow.Calls.Should().BeEmpty("a departure three days out is beyond a two-day lookahead");

        var wide = new FakeAlertsClient();
        await Service(context, wide, lookaheadDays: 4).SyncUpcomingLocationsAsync(CancellationToken.None);
        wide.Calls.Should().Contain("create:Ba Na Hills");
    }

    [Fact]
    public async Task ActiveDeparture_StaysMonitored()
    {
        using var context = TestDbContext.Create(Guid.NewGuid().ToString());
        SeedDeparture(context, start: Now.AddHours(-2), requiredForWeather: true);
        await context.SaveChangesAsync();
        var client = new FakeAlertsClient();

        await Service(context, client).SyncUpcomingLocationsAsync(CancellationToken.None);

        client.Calls.Should().Contain("create:Ba Na Hills");
    }

    [Fact]
    public async Task RetiredPointNeededAgain_GetsAFreshLocation()
    {
        using var context = TestDbContext.Create(Guid.NewGuid().ToString());
        SeedDeparture(context, start: Now.AddDays(1), requiredForWeather: true);
        var retired = WeatherLocationMapping.Create(
            "poi-100", "loc-deleted", 15.9988m, 108.0012m, "Ba Na Hills", Now.AddDays(-5), Now.AddDays(-10));
        retired.Deactivate(Now.AddDays(-3));
        context.WeatherLocationMappings.Add(retired);
        await context.SaveChangesAsync();
        var client = new FakeAlertsClient();

        await Service(context, client).SyncUpcomingLocationsAsync(CancellationToken.None);

        client.Calls.Should().Equal("create:Ba Na Hills", "list-linked", "link:loc-1");
        var mapping = await context.WeatherLocationMappings.SingleAsync();
        mapping.IsActive.Should().BeTrue();
        mapping.TomorrowLocationId.Should().Be("loc-1");
    }

    [Fact]
    public async Task DeparturesSharingAStop_CreateOneLocation()
    {
        using var context = TestDbContext.Create(Guid.NewGuid().ToString());
        var schedule = SeedDeparture(context, start: Now.AddDays(1), requiredForWeather: true);
        var second = TourSchedule.Create(Now.AddDays(2), Now.AddDays(2).AddHours(8), "City Center", 20, Now);
        typeof(TourSchedule).GetProperty(nameof(TourSchedule.Id))!.SetValue(second, 102L);
        typeof(TourSchedule).GetProperty(nameof(TourSchedule.TourId))!.SetValue(second, schedule.TourId);
        typeof(TourSchedule).GetProperty(nameof(TourSchedule.Tour))!.SetValue(second, schedule.Tour);
        context.TourSchedules.Add(second);
        await context.SaveChangesAsync();
        var client = new FakeAlertsClient();

        var result = await Service(context, client).SyncUpcomingLocationsAsync(CancellationToken.None);

        result.LocationsCreated.Should().Be(1);
        (await context.WeatherLocationMappings.CountAsync()).Should().Be(1);
    }

    private static TourSchedule SeedDeparture(TestDbContext context, DateTimeOffset start, bool? requiredForWeather)
    {
        var tour = (Tour)Activator.CreateInstance(typeof(Tour), nonPublic: true)!;
        typeof(Tour).GetProperty(nameof(Tour.Id))!.SetValue(tour, 1L);
        typeof(Tour).GetProperty(nameof(Tour.Title))!.SetValue(tour, "Da Nang Highlights");
        typeof(Tour).GetProperty(nameof(Tour.Status))!.SetValue(tour, TourStatus.Published);
        context.Tours.Add(tour);
        AddStop(context, tour, poiId: 100, "Ba Na Hills", requiredForWeather);

        var schedule = TourSchedule.Create(start, start.AddHours(8), "City Center", 20, Now.AddDays(-30));
        typeof(TourSchedule).GetProperty(nameof(TourSchedule.Id))!.SetValue(schedule, 101L);
        typeof(TourSchedule).GetProperty(nameof(TourSchedule.TourId))!.SetValue(schedule, tour.Id);
        typeof(TourSchedule).GetProperty(nameof(TourSchedule.Tour))!.SetValue(schedule, tour);
        context.TourSchedules.Add(schedule);
        return schedule;
    }

    private static void AddStop(TestDbContext context, Tour tour, long poiId, string name, bool? requiredForWeather)
    {
        var category = PoiCategory.Create("Landmark", null);
        var poi = PointOfInterest.Create(category, name, 15.9988m + poiId / 10000m, 108.0012m, 1, Now, "Address", "Desc");
        typeof(PointOfInterest).GetProperty(nameof(PointOfInterest.Id))!.SetValue(poi, poiId);
        var item = TourItineraryItem.Create(1, (int)(poiId / 100), poi.Id, null, 120, "Visit", requiredForWeather);
        typeof(TourItineraryItem).GetProperty(nameof(TourItineraryItem.TourId))!.SetValue(item, tour.Id);
        typeof(TourItineraryItem).GetProperty(nameof(TourItineraryItem.Tour))!.SetValue(item, tour);
        typeof(TourItineraryItem).GetProperty(nameof(TourItineraryItem.Poi))!.SetValue(item, poi);
        var items = (List<TourItineraryItem>)typeof(Tour).GetField("_itineraryItems",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(tour)!;
        items.Add(item);
        context.PointsOfInterest.Add(poi);
        context.TourItineraryItems.Add(item);
    }
}