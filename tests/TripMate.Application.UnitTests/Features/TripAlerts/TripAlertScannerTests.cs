using FluentAssertions;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

using TripMate.Application.Features.TripAlerts.Common;
using TripMate.Application.Features.TripAlerts.Scanning;
using TripMate.Application.Features.TripAlerts.Weather;
using TripMate.Application.Features.TripAlerts.WeatherAdvisories;
using TripMate.Application.UnitTests.TestUtilities;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Application.UnitTests.Features.TripAlerts;

public sealed class TripAlertScannerTests
{
    private const long TravelerId = 93;
    private static readonly DateTimeOffset Now = new(2026, 10, 22, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Scan_CreatesDelayAndInactivePoiOnceThenResolvesThemWhenProgressAndPoiRecover()
    {
        await using var db = TestDbContext.Create();
        var seed = await SeedAsync(db);
        var scanner = new TripAlertScanner(
            db,
            new FakeDateTimeProvider { UtcNow = Now },
            Options.Create(new TripAlertOptions()),
            new NoWeatherProvider(),
            new WeatherAlertClassifier(Options.Create(new TripAlertOptions())));

        await scanner.ScanAsync(CancellationToken.None);
        await scanner.ScanAsync(CancellationToken.None);

        (await db.Incidents.CountAsync()).Should().Be(2);
        (await db.Notifications.CountAsync(notification => notification.Type == Notification.TripAlertType))
            .Should().Be(2);
        var session = await db.TripSessions.Include(candidate => candidate.Items)
            .SingleAsync(candidate => candidate.Id == seed.SessionId);
        session.ReachItem(seed.ItemId, Now.AddMinutes(1));
        var poi = await db.PointsOfInterest.SingleAsync(candidate => candidate.Id == seed.PoiId);
        db.Entry(poi).Property(candidate => candidate.Status).CurrentValue = PointOfInterestStatus.Active;
        await db.SaveChangesAsync();

        await scanner.ScanAsync(CancellationToken.None);

        (await db.Incidents.CountAsync(incident => incident.ResolvedAtUtc != null)).Should().Be(2);
    }

    [Fact]
    public async Task WeatherAdvisories_ReturnsModerateRainWithoutPersistingAnAlert()
    {
        await using var db = TestDbContext.Create();
        var seed = await SeedAsync(db);
        var handler = new GetWeatherAdvisoriesQueryHandler(
            db,
            new FixedWeatherProvider(new TripWeatherSnapshot(
                Now, 4.5, false, "moderate rain", [])),
            new WeatherAlertClassifier(Options.Create(new TripAlertOptions())));

        var result = await handler.Handle(
            new GetWeatherAdvisoriesQuery(seed.SessionId, TravelerId), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().ContainSingle().Which.MessageCode
            .Should().Be(WeatherAdvisoryResponse.ModerateRainMessageCode);
        (await db.Incidents.CountAsync()).Should().Be(0);
        (await db.Notifications.CountAsync()).Should().Be(0);
    }

    private static async Task<Seed> SeedAsync(TestDbContext db)
    {
        var user = new User
        {
            Id = TravelerId,
            Email = "scanner@example.com",
            FullName = "Scanner",
            Role = UserRole.Traveler,
            Status = AccountStatus.Active,
            CreatedAtUtc = Now,
            UpdatedAtUtc = Now,
        };
        var category = PoiCategory.Create("Scanner", null);
        db.AddRange(user, category);
        await db.SaveChangesAsync();
        var poi = PointOfInterest.Create(category, "Closed POI", 16m, 108m, TravelerId, Now);
        db.Entry(poi).Property(candidate => candidate.Status).CurrentValue = PointOfInterestStatus.Inactive;
        db.Add(poi);
        await db.SaveChangesAsync();
        var itinerary = Itinerary.CreateManual(TravelerId, "Scanner", Itinerary.ActiveStatus, Now);
        itinerary.AddItem(ItineraryItem.CreateVisit(
            1, poi.Id, Now.AddHours(-1), Now.AddMinutes(-30), true, 0, "Planned"));
        db.Add(itinerary);
        await db.SaveChangesAsync();
        var item = itinerary.Items.Single();
        var session = TripSession.Start(
            itinerary.Id, itinerary.Id, TravelerId, Guid.NewGuid(), Now.AddHours(-2), Now.AddHours(3),
            [TripSessionItem.Snapshot(
                item.Id, 1, poi.Id, poi.Name, poi.Latitude, poi.Longitude,
                item.PlannedArrivalUtc!.Value, item.PlannedDepartureUtc!.Value, true)]);
        db.Add(session);
        await db.SaveChangesAsync();
        db.ClearTrackedEntities();
        return new Seed(session.Id, item.Id, poi.Id);
    }

    private sealed class NoWeatherProvider : ITripWeatherProvider
    {
        public Task<TripWeatherSnapshot?> GetAsync(
            WeatherForecastRequest request,
            CancellationToken cancellationToken) => Task.FromResult<TripWeatherSnapshot?>(null);
    }

    private sealed class FixedWeatherProvider(TripWeatherSnapshot snapshot) : ITripWeatherProvider
    {
        public Task<TripWeatherSnapshot?> GetAsync(
            WeatherForecastRequest request,
            CancellationToken cancellationToken) => Task.FromResult<TripWeatherSnapshot?>(snapshot);
    }

    private sealed record Seed(long SessionId, long ItemId, long PoiId);
}