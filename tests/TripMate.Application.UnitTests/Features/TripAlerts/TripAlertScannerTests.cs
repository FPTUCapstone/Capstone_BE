using FluentAssertions;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

using TripMate.Application.Features.TripAlerts.Common;
using TripMate.Application.Features.TripAlerts.List;
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
            new NoWeatherProvider());

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
        var seed = await SeedAsync(db, Now.AddHours(2), reviewedOutdoor: true);
        var handler = new GetWeatherAdvisoriesQueryHandler(
            db,
            new FixedWeatherProvider(new TripWeatherSnapshot(
                Now, 4.5, false, "moderate rain", [])),
            new FakeDateTimeProvider { UtcNow = Now },
            Options.Create(new TripAlertOptions()));

        var result = await handler.Handle(
            new GetWeatherAdvisoriesQuery(seed.SessionId, TravelerId), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().ContainSingle().Which.MessageCode
            .Should().Be(WeatherAdvisoryResponse.ModerateRainMessageCode);
        (await db.Incidents.CountAsync()).Should().Be(0);
        (await db.Notifications.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task WeatherAdvisories_HidesPersistedPreparationAlert()
    {
        await using var db = TestDbContext.Create();
        var seed = await SeedAsync(db, Now.AddMinutes(45), reviewedOutdoor: true);
        var provider = new FixedWeatherProvider(new TripWeatherSnapshot(Now, 5, false, null, []));
        var clock = new FakeDateTimeProvider { UtcNow = Now };
        var options = Options.Create(new TripAlertOptions());
        await new TripAlertScanner(db, clock, options, provider).ScanAsync(CancellationToken.None);

        var result = await new GetWeatherAdvisoriesQueryHandler(db, provider, clock, options)
            .Handle(new GetWeatherAdvisoriesQuery(seed.SessionId, TravelerId), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().BeEmpty();
    }

    [Fact]
    public async Task Scan_WeatherForecastCreatesOneInfoAlertWithinOneHour()
    {
        await using var db = TestDbContext.Create();
        var seed = await SeedAsync(db, Now.AddMinutes(45), reviewedOutdoor: true);
        var options = Options.Create(new TripAlertOptions());
        var scanner = new TripAlertScanner(db,
            new FakeDateTimeProvider { UtcNow = Now }, options,
            new FixedWeatherProvider(new TripWeatherSnapshot(Now, 5, false, null, [])));

        await scanner.ScanAsync(CancellationToken.None);
        await scanner.ScanAsync(CancellationToken.None);

        var alerts = await db.Incidents.Where(i => i.IncidentType == Incident.WeatherAdvisoryType)
            .ToArrayAsync();
        alerts.Should().ContainSingle();
        alerts[0].WeatherBasis.Should().Be(TripWeatherBasis.ForecastAtPlannedTime.ToString());
        alerts[0].RainIntensityMmPerHour.Should().Be(5);
        var list = await new ListTripAlertsQueryHandler(db)
            .Handle(new ListTripAlertsQuery(seed.SessionId, TravelerId), CancellationToken.None);
        var alert = list.Value.Single(value => value.IncidentId == alerts[0].Id);
        alert.Severity.Should().Be(TripAlertResponse.InfoSeverity);
        alert.MessageCode.Should().Be(TripAlertResponse.WeatherAdvisoryMessageCode);
        alert.WeatherBasis.Should().Be(TripWeatherBasis.ForecastAtPlannedTime.ToString());
        alert.RainIntensityMmPerHour.Should().Be(5);
        alert.WeatherCondition.Should().Be("ModerateRain");
        (await db.Notifications.CountAsync(n => n.Type == Notification.TripAlertType
            && n.RelatedEntityId == alerts[0].Id))
            .Should().Be(1);
    }

    [Fact]
    public async Task Scan_UnreviewedOutdoorDoesNotPushHeavyRain()
    {
        await using var db = TestDbContext.Create();
        await SeedAsync(db, Now.AddMinutes(45));
        var options = Options.Create(new TripAlertOptions());
        var scanner = new TripAlertScanner(db,
            new FakeDateTimeProvider { UtcNow = Now }, options,
            new FixedWeatherProvider(new TripWeatherSnapshot(Now, 20, false, null, [])));

        await scanner.ScanAsync(CancellationToken.None);

        (await db.Incidents.CountAsync(i => i.IncidentType == Incident.SevereWeatherType))
            .Should().Be(0);
    }

    [Fact]
    public async Task Scan_HeavyRainEscalationResolvesModerateAndCreatesOneNewWarning()
    {
        await using var db = TestDbContext.Create();
        await SeedAsync(db, Now.AddMinutes(45), reviewedOutdoor: true);
        var provider = new MutableWeatherProvider(new TripWeatherSnapshot(Now, 5, false, null, []));
        var scanner = new TripAlertScanner(db, new FakeDateTimeProvider { UtcNow = Now },
            Options.Create(new TripAlertOptions()), provider);
        await scanner.ScanAsync(CancellationToken.None);

        provider.Snapshot = new TripWeatherSnapshot(Now, 20, false, null, []);
        await scanner.ScanAsync(CancellationToken.None);
        await scanner.ScanAsync(CancellationToken.None);

        (await db.Incidents.CountAsync(i => i.IncidentType == Incident.SevereWeatherType))
            .Should().Be(1);
        (await db.Incidents.CountAsync(i => i.IncidentType == Incident.WeatherAdvisoryType
            && i.ResolvedAtUtc != null)).Should().Be(1);
        var sessionId = await db.TripSessions.Select(s => s.Id).SingleAsync();
        var list = await new ListTripAlertsQueryHandler(db)
            .Handle(new ListTripAlertsQuery(sessionId, TravelerId), CancellationToken.None);
        list.Value.Single(alert => alert.Type == Incident.SevereWeatherType)
            .CanSkipAffectedStop.Should().BeTrue();
        list.Value.Single(alert => alert.Type == Incident.SevereWeatherType)
            .WeatherCondition.Should().Be("HeavyRain");
    }

    [Fact]
    public async Task Scan_ThunderstormAtIndoorPoiDoesNotOfferOutdoorSkipShortcut()
    {
        await using var db = TestDbContext.Create();
        var seed = await SeedAsync(db, Now.AddMinutes(45));
        var poi = await db.PointsOfInterest.SingleAsync(p => p.Id == seed.PoiId);
        poi.ReviewIndoorOutdoor(IndoorOutdoorType.Indoor, Now);
        await db.SaveChangesAsync();
        var provider = new FixedWeatherProvider(new TripWeatherSnapshot(Now, 20, true, null, []));
        await new TripAlertScanner(db, new FakeDateTimeProvider { UtcNow = Now },
            Options.Create(new TripAlertOptions()), provider).ScanAsync(CancellationToken.None);

        var list = await new ListTripAlertsQueryHandler(db)
            .Handle(new ListTripAlertsQuery(seed.SessionId, TravelerId), CancellationToken.None);
        list.Value.Single(alert => alert.Type == Incident.SevereWeatherType)
            .CanSkipAffectedStop.Should().BeFalse();
        list.Value.Single(alert => alert.Type == Incident.SevereWeatherType)
            .WeatherCondition.Should().Be("Thunderstorm");
    }

    [Fact]
    public async Task Scan_ExtremeRainAtReviewedOutdoorOffersSkipShortcut()
    {
        await using var db = TestDbContext.Create();
        var seed = await SeedAsync(db, Now.AddMinutes(45), reviewedOutdoor: true);
        var provider = new FixedWeatherProvider(new TripWeatherSnapshot(Now, 55, false, null, []));
        await new TripAlertScanner(db, new FakeDateTimeProvider { UtcNow = Now },
            Options.Create(new TripAlertOptions()), provider).ScanAsync(CancellationToken.None);

        var list = await new ListTripAlertsQueryHandler(db)
            .Handle(new ListTripAlertsQuery(seed.SessionId, TravelerId), CancellationToken.None);
        list.Value.Single(alert => alert.Type == Incident.SevereWeatherType)
            .CanSkipAffectedStop.Should().BeTrue();
        list.Value.Single(alert => alert.Type == Incident.SevereWeatherType)
            .WeatherCondition.Should().Be("ExtremeRain");
    }

    [Fact]
    public async Task Scan_ProviderTimeoutKeepsExistingWeatherWarningOpen()
    {
        await using var db = TestDbContext.Create();
        await SeedAsync(db, Now.AddMinutes(45), reviewedOutdoor: true);
        var provider = new MutableWeatherProvider(new TripWeatherSnapshot(Now, 20, false, null, []));
        var scanner = new TripAlertScanner(db, new FakeDateTimeProvider { UtcNow = Now },
            Options.Create(new TripAlertOptions()), provider);
        await scanner.ScanAsync(CancellationToken.None);

        provider.Timeout = true;
        await scanner.ScanAsync(CancellationToken.None);

        (await db.Incidents.SingleAsync(i => i.IncidentType == Incident.SevereWeatherType))
            .ResolvedAtUtc.Should().BeNull();
    }

    [Fact]
    public async Task Scan_OverdueStopUsesCurrentDestinationConditions()
    {
        await using var db = TestDbContext.Create();
        await SeedAsync(db, Now.AddMinutes(-5), reviewedOutdoor: true);
        var provider = new MutableWeatherProvider(new TripWeatherSnapshot(Now, 20, false, null, [],
            TripWeatherBasis.CurrentAtDestination));
        var scanner = new TripAlertScanner(db, new FakeDateTimeProvider { UtcNow = Now },
            Options.Create(new TripAlertOptions()), provider);

        await scanner.ScanAsync(CancellationToken.None);

        provider.LastRequest!.UseRealtime.Should().BeTrue();
        var weather = await db.Incidents.SingleAsync(i => i.IncidentType == Incident.SevereWeatherType);
        weather.WeatherBasis.Should().Be(TripWeatherBasis.CurrentAtDestination.ToString());
    }

    [Fact]
    public async Task Scan_ExploringPastPlannedDepartureThreshold_CreatesStopOverstayAlertOnceAndExposesPlannedDeparture()
    {
        await using var db = TestDbContext.Create();
        var plannedArrival = Now.AddMinutes(-90);
        var plannedDeparture = Now.AddMinutes(-20);
        var seed = await SeedTwoStopsAsync(db, plannedArrival, plannedDeparture, Now.AddMinutes(10), Now.AddMinutes(70));
        var session = await db.TripSessions.Include(s => s.Items).SingleAsync(s => s.Id == seed.SessionId);
        session.ReachItem(seed.FirstItemId, plannedArrival);
        await db.SaveChangesAsync();

        var scanner = new TripAlertScanner(
            db,
            new FakeDateTimeProvider { UtcNow = Now },
            Options.Create(new TripAlertOptions()),
            new NoWeatherProvider());

        await scanner.ScanAsync(CancellationToken.None);
        await scanner.ScanAsync(CancellationToken.None);

        var list = await new ListTripAlertsQueryHandler(db)
            .Handle(new ListTripAlertsQuery(seed.SessionId, TravelerId), CancellationToken.None);
        list.IsSuccess.Should().BeTrue();
        var alert = list.Value.Should().ContainSingle().Subject;
        alert.Type.Should().Be(Incident.ScheduleDelayType);
        alert.Severity.Should().Be(TripAlertResponse.WarningSeverity);
        alert.MessageCode.Should().Be(TripAlertResponse.StopOverstayMessageCode);
        alert.ResolvedAtUtc.Should().BeNull();
        alert.AffectedItem.Should().NotBeNull();
        alert.AffectedItem!.ItemId.Should().Be(seed.FirstItemId);
        alert.AffectedItem.PlannedDepartureUtc.Should().Be(plannedDeparture);
    }

    [Fact]
    public async Task Scan_ExploringWithinOverstayThreshold_DoesNotCreateOverstayAlert()
    {
        await using var db = TestDbContext.Create();
        var plannedArrival = Now.AddMinutes(-75);
        var plannedDeparture = Now.AddMinutes(-15); // Exactly on the 15-minute boundary (not strictly past).
        var seed = await SeedTwoStopsAsync(db, plannedArrival, plannedDeparture, Now.AddMinutes(15), Now.AddMinutes(75));
        var session = await db.TripSessions.Include(s => s.Items).SingleAsync(s => s.Id == seed.SessionId);
        session.ReachItem(seed.FirstItemId, plannedArrival);
        await db.SaveChangesAsync();

        var scanner = new TripAlertScanner(
            db,
            new FakeDateTimeProvider { UtcNow = Now },
            Options.Create(new TripAlertOptions()),
            new NoWeatherProvider());

        await scanner.ScanAsync(CancellationToken.None);

        (await db.Incidents.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Scan_OverstayAlertRemainsOpenWhileExploringAndResolvesOnDepart()
    {
        await using var db = TestDbContext.Create();
        var plannedArrival = Now.AddMinutes(-90);
        var plannedDeparture = Now.AddMinutes(-20);
        var seed = await SeedTwoStopsAsync(db, plannedArrival, plannedDeparture, Now.AddMinutes(10), Now.AddMinutes(70));
        var session = await db.TripSessions.Include(s => s.Items).SingleAsync(s => s.Id == seed.SessionId);
        session.ReachItem(seed.FirstItemId, plannedArrival);
        await db.SaveChangesAsync();

        var clock = new FakeDateTimeProvider { UtcNow = Now };
        var scanner = new TripAlertScanner(db, clock, Options.Create(new TripAlertOptions()), new NoWeatherProvider());
        await scanner.ScanAsync(CancellationToken.None);

        clock.UtcNow = Now.AddMinutes(10);
        await scanner.ScanAsync(CancellationToken.None);
        (await db.Incidents.SingleAsync()).ResolvedAtUtc.Should().BeNull();

        session = await db.TripSessions.Include(s => s.Items).SingleAsync(s => s.Id == seed.SessionId);
        session.Depart(Now.AddMinutes(11));
        await db.SaveChangesAsync();

        clock.UtcNow = Now.AddMinutes(12);
        await scanner.ScanAsync(CancellationToken.None);

        var resolved = await db.Incidents.SingleAsync();
        resolved.ResolvedAtUtc.Should().Be(Now.AddMinutes(12));
    }

    [Fact]
    public async Task Scan_FinalStopExploringPastDeparture_StillCreatesStopOverstayAlert()
    {
        await using var db = TestDbContext.Create();
        // Stop 1 is skipped and stop 2 is reached: zero Pending items remain while still Exploring stop 2.
        var seed = await SeedTwoStopsAsync(
            db,
            firstArrivalUtc: Now.AddMinutes(-150),
            firstDepartureUtc: Now.AddMinutes(-100),
            secondArrivalUtc: Now.AddMinutes(-80),
            secondDepartureUtc: Now.AddMinutes(-20));
        var session = await db.TripSessions.Include(s => s.Items).SingleAsync(s => s.Id == seed.SessionId);
        session.SkipItem(seed.FirstItemId, Now.AddMinutes(-140));
        session.ReachItem(seed.SecondItemId, Now.AddMinutes(-80));
        await db.SaveChangesAsync();

        var scanner = new TripAlertScanner(
            db,
            new FakeDateTimeProvider { UtcNow = Now },
            Options.Create(new TripAlertOptions()),
            new NoWeatherProvider());

        await scanner.ScanAsync(CancellationToken.None);

        var list = await new ListTripAlertsQueryHandler(db)
            .Handle(new ListTripAlertsQuery(seed.SessionId, TravelerId), CancellationToken.None);
        var alert = list.Value.Should().ContainSingle().Subject;
        alert.MessageCode.Should().Be(TripAlertResponse.StopOverstayMessageCode);
        alert.AffectedItem!.ItemId.Should().Be(seed.SecondItemId);
        alert.AffectedItem.PlannedDepartureUtc.Should().Be(Now.AddMinutes(-20));
        alert.ResolvedAtUtc.Should().BeNull();
    }

    [Fact]
    public async Task Scan_WhileExploring_SuppressesArrivalDelayForNextStopUntilDepart()
    {
        await using var db = TestDbContext.Create();
        // Both stop 1 departure (Now - 60m) and stop 2 arrival (Now - 40m) are well past their thresholds.
        var seed = await SeedTwoStopsAsync(
            db,
            firstArrivalUtc: Now.AddMinutes(-120),
            firstDepartureUtc: Now.AddMinutes(-60),
            secondArrivalUtc: Now.AddMinutes(-40),
            secondDepartureUtc: Now.AddMinutes(20));
        var session = await db.TripSessions.Include(s => s.Items).SingleAsync(s => s.Id == seed.SessionId);
        session.ReachItem(seed.FirstItemId, Now.AddMinutes(-120));
        await db.SaveChangesAsync();

        var clock = new FakeDateTimeProvider { UtcNow = Now };
        var scanner = new TripAlertScanner(db, clock, Options.Create(new TripAlertOptions()), new NoWeatherProvider());
        await scanner.ScanAsync(CancellationToken.None);

        var whileExploring = await new ListTripAlertsQueryHandler(db)
            .Handle(new ListTripAlertsQuery(seed.SessionId, TravelerId), CancellationToken.None);
        whileExploring.Value.Should().ContainSingle()
            .Which.MessageCode.Should().Be(TripAlertResponse.StopOverstayMessageCode);

        session = await db.TripSessions.Include(s => s.Items).SingleAsync(s => s.Id == seed.SessionId);
        session.Depart(Now.AddMinutes(1));
        await db.SaveChangesAsync();

        clock.UtcNow = Now.AddMinutes(2);
        await scanner.ScanAsync(CancellationToken.None);

        var afterDepart = await new ListTripAlertsQueryHandler(db)
            .Handle(new ListTripAlertsQuery(seed.SessionId, TravelerId), CancellationToken.None);
        afterDepart.Value.Should().HaveCount(2);
        afterDepart.Value.Single(a => a.MessageCode == TripAlertResponse.StopOverstayMessageCode)
            .ResolvedAtUtc.Should().NotBeNull();
        var nextDelay = afterDepart.Value.Single(a => a.MessageCode == TripAlertResponse.ScheduleDelayMessageCode);
        nextDelay.AffectedItem!.ItemId.Should().Be(seed.SecondItemId);
        nextDelay.ResolvedAtUtc.Should().BeNull();
    }

    private static async Task<Seed> SeedAsync(TestDbContext db,
        DateTimeOffset? plannedArrivalUtc = null, bool reviewedOutdoor = false)
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
        if (reviewedOutdoor)
            poi.ReviewIndoorOutdoor(IndoorOutdoorType.Outdoor, Now);
        db.Entry(poi).Property(candidate => candidate.Status).CurrentValue = PointOfInterestStatus.Inactive;
        db.Add(poi);
        await db.SaveChangesAsync();
        var itinerary = Itinerary.CreateManual(TravelerId, "Scanner", Itinerary.ActiveStatus, Now);
        itinerary.AddItem(ItineraryItem.CreateVisit(
            1, poi.Id, plannedArrivalUtc ?? Now.AddHours(-1),
            (plannedArrivalUtc ?? Now.AddHours(-1)).AddMinutes(30), true, 0, "Planned"));
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

    private sealed class MutableWeatherProvider(TripWeatherSnapshot snapshot) : ITripWeatherProvider
    {
        public TripWeatherSnapshot Snapshot { get; set; } = snapshot;
        public bool Timeout { get; set; }
        public WeatherForecastRequest? LastRequest { get; private set; }
        public Task<TripWeatherSnapshot?> GetAsync(WeatherForecastRequest request,
            CancellationToken cancellationToken)
        {
            if (Timeout) throw new TaskCanceledException("Provider timeout.");
            LastRequest = request;
            return Task.FromResult<TripWeatherSnapshot?>(Snapshot);
        }
    }

    private static async Task<TwoStopSeed> SeedTwoStopsAsync(
        TestDbContext db,
        DateTimeOffset firstArrivalUtc,
        DateTimeOffset firstDepartureUtc,
        DateTimeOffset secondArrivalUtc,
        DateTimeOffset secondDepartureUtc)
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
        var category = PoiCategory.Create("Culture", null);
        db.AddRange(user, category);
        await db.SaveChangesAsync();
        var firstPoi = PointOfInterest.Create(category, "Cham Museum", 16.0605m, 108.2235m, TravelerId, Now);
        var secondPoi = PointOfInterest.Create(category, "Dragon Bridge", 16.0612m, 108.2277m, TravelerId, Now);
        db.AddRange(firstPoi, secondPoi);
        await db.SaveChangesAsync();
        var itinerary = Itinerary.CreateManual(TravelerId, "Overstay Trip", Itinerary.ActiveStatus, Now);
        itinerary.AddItem(ItineraryItem.CreateVisit(1, firstPoi.Id, firstArrivalUtc, firstDepartureUtc, true, 0, "Stop 1"));
        itinerary.AddItem(ItineraryItem.CreateVisit(2, secondPoi.Id, secondArrivalUtc, secondDepartureUtc, false, 0, "Stop 2"));
        db.Add(itinerary);
        await db.SaveChangesAsync();
        var ordered = itinerary.Items.OrderBy(i => i.SequenceNo).ToArray();
        var session = TripSession.Start(
            itinerary.Id,
            itinerary.Id,
            TravelerId,
            Guid.NewGuid(),
            firstArrivalUtc.AddMinutes(-30),
            secondDepartureUtc.AddHours(2),
            [
                TripSessionItem.Snapshot(
                    ordered[0].Id, 1, firstPoi.Id, firstPoi.Name, firstPoi.Latitude, firstPoi.Longitude,
                    firstArrivalUtc, firstDepartureUtc, true),
                TripSessionItem.Snapshot(
                    ordered[1].Id, 2, secondPoi.Id, secondPoi.Name, secondPoi.Latitude, secondPoi.Longitude,
                    secondArrivalUtc, secondDepartureUtc, false),
            ]);
        db.Add(session);
        await db.SaveChangesAsync();
        db.ClearTrackedEntities();
        return new TwoStopSeed(session.Id, ordered[0].Id, ordered[1].Id);
    }

    private sealed record Seed(long SessionId, long ItemId, long PoiId);
    private sealed record TwoStopSeed(long SessionId, long FirstItemId, long SecondItemId);
}