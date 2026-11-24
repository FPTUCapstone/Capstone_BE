using FluentAssertions;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

using TripMate.Application.Features.Rerouting.Common;
using TripMate.Application.Features.Rerouting.Generate;
using TripMate.Application.Features.TripAlerts.Common;
using TripMate.Application.Features.TripAlerts.List;
using TripMate.Application.Features.TripAlerts.ReportDeviation;
using TripMate.Application.Features.TripAlerts.Scanning;
using TripMate.Application.Features.TripAlerts.Weather;
using TripMate.Application.UnitTests.Features.Rerouting.Engine;
using TripMate.Application.UnitTests.TestUtilities;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

using static TripMate.Application.UnitTests.Features.Rerouting.ReroutingProposalSweeperTests;

namespace TripMate.Application.UnitTests.Features.Rerouting;

/// <summary>UC-15 T5: the scan, the deviation report and the alert list use the engine without ever risking the alert.</summary>
public sealed class ReroutingTriggerTests
{
    private static readonly DateTimeOffset Now = ReroutingTestKit.RequestStart.AddMinutes(30);

    // ---------------------------------------------------------------- scanner

    [Fact]
    public async Task Scan_CreatesTheClosureAlertThenTheProposalAndTheAlertListPointsAtIt()
    {
        await using var kit = await ReroutingTestKit.CreateAsync();
        var session = await ClosureScenarioAsync(kit);
        var clock = new FakeDateTimeProvider { UtcNow = Now };
        var scanner = Scanner(kit, clock, ReroutingProposalGeneratorTests.Generator(kit));

        await scanner.ScanAsync(CancellationToken.None);
        await scanner.ScanAsync(CancellationToken.None);

        var incident = await kit.Db.Incidents.SingleAsync();
        incident.IncidentType.Should().Be(Incident.PoiClosureType);
        var proposal = await kit.Db.ReroutingEvents.SingleAsync();
        proposal.IncidentId.Should().Be(incident.Id);
        var list = await new ListTripAlertsQueryHandler(kit.Db, clock, Options.Create(new ReroutingOptions()))
            .Handle(new ListTripAlertsQuery(session.Id, ReroutingTestKit.TravelerId), CancellationToken.None);
        list.Value.Single().ReroutingProposalId.Should().Be(proposal.Id);
    }

    [Fact]
    public async Task Scan_CallsTheGeneratorOncePerNewIncidentAfterItsTransactionCommitted()
    {
        await using var kit = await ReroutingTestKit.CreateAsync();
        await ClosureScenarioAsync(kit);
        var committed = false;
        kit.Db.OnTransactionCompleted = () => committed = true;
        var incidentWasCommitted = false;
        var spy = new SpyGenerator { OnCall = _ => incidentWasCommitted = committed };

        var scanner = Scanner(kit, new FakeDateTimeProvider { UtcNow = Now }, spy);
        await scanner.ScanAsync(CancellationToken.None);
        await scanner.ScanAsync(CancellationToken.None);

        spy.IncidentIds.Should().ContainSingle("the second scan finds the open incident and does not create another");
        spy.IncidentIds[0].Should().Be((await kit.Db.Incidents.SingleAsync()).Id);
        spy.Times.Should().ContainSingle().Which.Should().Be(Now);
        incidentWasCommitted.Should().BeTrue();
    }

    [Fact]
    public async Task Scan_AGeneratorFailureNeverStopsTheScanOrLosesTheAlert()
    {
        await using var kit = await ReroutingTestKit.CreateAsync();
        await ClosureScenarioAsync(kit);
        var spy = new SpyGenerator { ThrowFor = 1 };

        var act = () => Scanner(kit, new FakeDateTimeProvider { UtcNow = Now }, spy).ScanAsync(CancellationToken.None);

        await act.Should().NotThrowAsync();
        (await kit.Db.Incidents.CountAsync()).Should().Be(1);
        (await kit.Db.Notifications.CountAsync(notification => notification.Type == Notification.TripAlertType)).Should().Be(1);
    }

    [Fact]
    public async Task Scan_RunsTheSweepEveryTimeWithTheScanClock()
    {
        await using var kit = await ReroutingTestKit.CreateAsync();
        await ClosureScenarioAsync(kit);
        var sweeper = new SpySweeper();

        var scanner = new TripAlertScanner(
            kit.Db, new FakeDateTimeProvider { UtcNow = Now }, Options.Create(new TripAlertOptions()),
            new NullWeatherProvider(), null, sweeper);
        await scanner.ScanAsync(CancellationToken.None);
        await scanner.ScanAsync(CancellationToken.None);

        sweeper.Times.Should().Equal(Now, Now);
    }

    [Fact]
    public async Task Scan_ASweepFailureNeverStopsTheScan()
    {
        await using var kit = await ReroutingTestKit.CreateAsync();
        await ClosureScenarioAsync(kit);

        var scanner = new TripAlertScanner(
            kit.Db, new FakeDateTimeProvider { UtcNow = Now }, Options.Create(new TripAlertOptions()),
            new NullWeatherProvider(), null, new SpySweeper { Throw = true });

        var act = () => scanner.ScanAsync(CancellationToken.None);

        await act.Should().NotThrowAsync();
        (await kit.Db.Incidents.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task Scan_ExpiresAnOverdueProposalThroughTheRealSweeper()
    {
        await using var kit = await ReroutingTestKit.CreateAsync();
        await ClosureScenarioAsync(kit);
        var clock = new FakeDateTimeProvider { UtcNow = Now };
        var generator = ReroutingProposalGeneratorTests.Generator(kit);
        var sweeper = new ReroutingProposalSweeper(kit.Db, generator, Options.Create(new ReroutingOptions()));
        var scanner = new TripAlertScanner(
            kit.Db, clock, Options.Create(new TripAlertOptions()), new NullWeatherProvider(), generator, sweeper);
        await scanner.ScanAsync(CancellationToken.None);
        (await kit.Db.ReroutingEvents.SingleAsync()).Status.Should().Be(ReroutingEvent.StatusProposed);

        clock.UtcNow = Now.AddMinutes(16);
        await scanner.ScanAsync(CancellationToken.None);

        (await kit.Db.ReroutingEvents.AsNoTracking().SingleAsync()).Status.Should().Be(ReroutingEvent.StatusExpired);
    }

    // ---------------------------------------------------------------- deviation report

    [Fact]
    public async Task ReportDeviation_NewIncident_CallsTheGeneratorAfterTheTransaction()
    {
        await using var kit = await ReroutingTestKit.CreateAsync();
        var session = await DeviationScenarioAsync(kit);
        var committed = false;
        kit.Db.OnTransactionCompleted = () => committed = true;
        var calledAfterCommit = false;
        var spy = new SpyGenerator { OnCall = _ => calledAfterCommit = committed };

        var result = await Handler(kit, spy).Handle(Command(kit, session), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        spy.IncidentIds.Should().ContainSingle().Which.Should().Be(result.Value.IncidentId);
        spy.Times.Should().ContainSingle().Which.Should().Be(Now);
        calledAfterCommit.Should().BeTrue();
    }

    [Fact]
    public async Task ReportDeviation_Replay_DoesNotCallTheGeneratorAgain()
    {
        await using var kit = await ReroutingTestKit.CreateAsync();
        var session = await DeviationScenarioAsync(kit);
        var spy = new SpyGenerator();
        var command = Command(kit, session);
        await Handler(kit, spy).Handle(command, CancellationToken.None);

        var replay = await Handler(kit, spy).Handle(command, CancellationToken.None);

        replay.IsSuccess.Should().BeTrue();
        spy.IncidentIds.Should().ContainSingle("a replay returns the stored alert without creating or proposing anything");
    }

    [Fact]
    public async Task ReportDeviation_ExistingOpenDeviationForTheTarget_DoesNotCallTheGenerator()
    {
        await using var kit = await ReroutingTestKit.CreateAsync();
        var session = await DeviationScenarioAsync(kit);
        var spy = new SpyGenerator();
        await Handler(kit, spy).Handle(Command(kit, session), CancellationToken.None);

        var second = await Handler(kit, spy).Handle(
            Command(kit, session) with { EventId = Guid.NewGuid() }, CancellationToken.None);

        second.IsSuccess.Should().BeTrue();
        spy.IncidentIds.Should().ContainSingle("the open deviation for this target is returned, nothing new is created");
    }

    [Fact]
    public async Task ReportDeviation_AGeneratorFailureStillReturnsTheAlert()
    {
        await using var kit = await ReroutingTestKit.CreateAsync();
        var session = await DeviationScenarioAsync(kit);

        var result = await Handler(kit, new SpyGenerator { ThrowFor = 1 }).Handle(Command(kit, session), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        (await kit.Db.Incidents.CountAsync()).Should().Be(1);
    }

    // ---------------------------------------------------------------- alert list

    [Theory]
    [InlineData(14, 59, true)]
    [InlineData(15, 0, true)]
    [InlineData(15, 1, false)]
    public async Task AlertList_HidesAProposalOnceItIsPastItsTtl(int minutes, int seconds, bool visible)
    {
        await using var kit = await ReroutingTestKit.CreateAsync();
        var session = await ClosureScenarioAsync(kit);
        var incident = (await kit.Db.Incidents.ToArrayAsync()).SingleOrDefault()
            ?? await kit.AddIncidentAsync(session, Incident.PoiClosureType, session.Items.First().ItineraryItemId);
        kit.Db.Notifications.Add(new Notification
        {
            UserId = ReroutingTestKit.TravelerId,
            Channel = NotificationChannel.Push,
            Type = Notification.TripAlertType,
            Title = "TripMate trip alert",
            Body = "Open TripMate to review an update to your active trip.",
            RelatedEntityType = Notification.IncidentRelatedEntityType,
            RelatedEntityId = incident.Id,
            Status = NotificationStatus.Pending,
            CreatedAtUtc = Now,
        });
        var proposal = ReroutingEvent.Propose(incident, session, "{\"schemaVersion\":1}", Now);
        kit.Db.ReroutingEvents.Add(proposal);
        await kit.Db.SaveChangesAsync();
        var clock = new FakeDateTimeProvider { UtcNow = Now.AddMinutes(minutes).AddSeconds(seconds) };

        var list = await new ListTripAlertsQueryHandler(kit.Db, clock, Options.Create(new ReroutingOptions()))
            .Handle(new ListTripAlertsQuery(session.Id, ReroutingTestKit.TravelerId), CancellationToken.None);

        list.Value.Single().ReroutingProposalId.Should().Be(visible ? proposal.Id : null);
    }

    // ---------------------------------------------------------------- helpers

    private static TripAlertScanner Scanner(ReroutingTestKit kit, FakeDateTimeProvider clock, IReroutingProposalGenerator generator) =>
        new(kit.Db, clock, Options.Create(new TripAlertOptions()), new NullWeatherProvider(), generator);

    private static ReportDeviationCommandHandler Handler(ReroutingTestKit kit, IReroutingProposalGenerator generator) =>
        new(kit.Db, new FakeDateTimeProvider { UtcNow = Now }, Options.Create(new TripAlertOptions()), generator);

    private static ReportDeviationCommand Command(ReroutingTestKit kit, TripSession session) =>
        new(session.Id, ReroutingTestKit.TravelerId, Guid.NewGuid(), Now, session.Items.OrderBy(item => item.SequenceNo).First().ItineraryItemId);

    /// <summary>[A, B, C] where B is closed at its planned time (opens 13:00) and another Culture POI is nearby.</summary>
    private static async Task<TripSession> ClosureScenarioAsync(ReroutingTestKit kit)
    {
        var a = await kit.AddPoiAsync("Stop A", ReroutingTestKit.NorthByKm(0));
        var b = await kit.AddPoiAsync("Stop B", ReroutingTestKit.NorthByKm(0.1), opensAt: new TimeOnly(13, 0), closesAt: new TimeOnly(20, 0));
        var c = await kit.AddPoiAsync("Stop C", ReroutingTestKit.NorthByKm(0.2));
        await kit.AddPoiAsync("Another museum", ReroutingTestKit.NorthByKm(0.4));
        await kit.AddRequestAsync();
        return await kit.StartSessionAsync(a, b, c);
    }

    private static async Task<TripSession> DeviationScenarioAsync(ReroutingTestKit kit)
    {
        var a = await kit.AddPoiAsync("Stop A", ReroutingTestKit.NorthByKm(0));
        var b = await kit.AddPoiAsync("Stop B", ReroutingTestKit.NorthByKm(0.1));
        await kit.AddRequestAsync();
        return await kit.StartSessionAsync(a, b);
    }

    private sealed class NullWeatherProvider : ITripWeatherProvider
    {
        public Task<TripWeatherSnapshot?> GetAsync(WeatherForecastRequest request, CancellationToken cancellationToken) =>
            Task.FromResult<TripWeatherSnapshot?>(null);
    }

    private sealed class SpySweeper : IReroutingProposalSweeper
    {
        public List<DateTimeOffset> Times { get; } = [];

        public bool Throw { get; init; }

        public Task SweepAsync(DateTimeOffset nowUtc, CancellationToken cancellationToken)
        {
            Times.Add(nowUtc);
            return Throw ? throw new InvalidOperationException("boom") : Task.CompletedTask;
        }
    }
}