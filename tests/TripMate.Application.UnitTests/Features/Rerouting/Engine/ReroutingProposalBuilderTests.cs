using FluentAssertions;

using Microsoft.Extensions.Options;

using TripMate.Application.Features.Rerouting.Common;
using TripMate.Application.Features.Rerouting.Engine;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Application.UnitTests.Features.Rerouting.Engine;

/// <summary>The engine end to end: real strategies, real evaluator, in-memory data.</summary>
public sealed class ReroutingProposalBuilderTests
{
    private static readonly DateTimeOffset Now = ReroutingTestKit.RequestStart.AddMinutes(30);

    [Fact]
    public async Task SevereWeather_ReplacesTheOutdoorStopWithANearbyIndoorOne()
    {
        await using var kit = await ReroutingTestKit.CreateAsync();
        var (a, b, c) = await ThreeStopsAsync(kit, bIndoorOutdoor: IndoorOutdoorType.Outdoor, reviewed: true);
        var indoor = await kit.AddPoiAsync("Cham Museum", ReroutingTestKit.NorthByKm(0.3), "Museum", IndoorOutdoorType.Indoor, reviewed: true);
        var session = await kit.StartSessionAsync(a, b, c);
        var incident = await kit.AddIncidentAsync(session, Incident.SevereWeatherType, kit.ItemIdOf(session, b));

        var snapshot = await Builder(kit).BuildAsync(incident.Id, Now, CancellationToken.None);

        snapshot.Should().NotBeNull();
        snapshot!.SchemaVersion.Should().Be(ReroutingProposalSnapshot.CurrentSchemaVersion);
        snapshot.Strategy.Should().Be(ReroutingStrategies.ReplaceWithIndoor);
        snapshot.ReasonCode.Should().Be("trip_alert.severe_weather");
        snapshot.ProposedStops.Select(stop => stop.PoiId).Should().Equal(a.Id, indoor.Id, c.Id);
        snapshot.ProposedStops.Select(stop => stop.Change).Should().Equal(
            ReroutingStopChanges.Kept, ReroutingStopChanges.Added, ReroutingStopChanges.Kept);
        snapshot.ProposedStops.Single(stop => stop.PoiId == indoor.Id).Name.Should().Be("Cham Museum");
        snapshot.RemovedPoiIds.Should().Equal(b.Id);
        snapshot.AffectedItineraryItemIds.Should().Equal(kit.ItemIdOf(session, b));
        snapshot.BaseItineraryId.Should().Be(session.ItineraryId);
        snapshot.BaseItineraryVersion.Should().Be(1);
        snapshot.BaseStops.Select(stop => stop.PoiId).Should().Equal(a.Id, b.Id, c.Id);
        snapshot.BaseStops.Select(stop => stop.ItineraryItemId).Should().Equal(session.Items.OrderBy(item => item.SequenceNo).Select(item => item.ItineraryItemId));
        snapshot.Anchor.StartAtUtc.Should().Be(Now);
        snapshot.ProposedStops[0].PlannedArrivalUtc.Should().BeOnOrAfter(Now);
        snapshot.EndAtUtc.Should().BeAfter(snapshot.ProposedStops[^1].PlannedDepartureUtc);
    }

    [Fact]
    public async Task SevereWeather_WhenTheIndoorCandidateIsClosedAtArrival_FallsBackToDroppingTheStop()
    {
        await using var kit = await ReroutingTestKit.CreateAsync();
        var (a, b, c) = await ThreeStopsAsync(kit, bIndoorOutdoor: IndoorOutdoorType.Outdoor, reviewed: true);
        _ = await kit.AddPoiAsync(
            "Evening gallery", ReroutingTestKit.NorthByKm(0.3), "Museum", IndoorOutdoorType.Indoor, reviewed: true,
            opensAt: new TimeOnly(18, 0), closesAt: new TimeOnly(20, 0));
        var session = await kit.StartSessionAsync(a, b, c);
        var incident = await kit.AddIncidentAsync(session, Incident.SevereWeatherType, kit.ItemIdOf(session, b));

        var snapshot = await Builder(kit).BuildAsync(incident.Id, Now, CancellationToken.None);

        snapshot.Should().NotBeNull();
        snapshot!.Strategy.Should().Be(ReroutingStrategies.Drop);
        snapshot.ProposedStops.Select(stop => stop.PoiId).Should().Equal(a.Id, c.Id);
        snapshot.RemovedPoiIds.Should().Equal(b.Id);
    }

    [Fact]
    public async Task Drop_ReportsTheTravelAndArrivalDifferencesAgainstTheCurrentPlan()
    {
        await using var kit = await ReroutingTestKit.CreateAsync();
        var (a, b, c) = await ThreeStopsAsync(kit, bIndoorOutdoor: IndoorOutdoorType.Outdoor, reviewed: true);
        var session = await kit.StartSessionAsync(a, b, c);
        var incident = await kit.AddIncidentAsync(session, Incident.SevereWeatherType, kit.ItemIdOf(session, b));

        var snapshot = await Builder(kit).BuildAsync(incident.Id, Now, CancellationToken.None);

        snapshot.Should().NotBeNull();
        snapshot!.Strategy.Should().Be(ReroutingStrategies.Drop);
        // Current remaining legs (A to B, B to C) take 20 minutes; the dropped plan has one leg (A to C) of 10.
        snapshot.TravelTimeDiffMinutes.Should().Be(-10);
        var lastCurrent = session.Items.OrderBy(item => item.SequenceNo).Last();
        var lastProposed = snapshot.ProposedStops[^1];
        snapshot.ArrivalTimeDiffMinutes.Should().Be(
            (int)Math.Round((lastProposed.PlannedArrivalUtc - lastCurrent.PlannedArrivalUtc).TotalMinutes));
        snapshot.ArrivalTimeDiffMinutes.Should().BeNegative("the proposed plan reaches its last stop earlier");
    }

    [Theory]
    [InlineData(IndoorOutdoorType.Indoor, true, false)]
    [InlineData(IndoorOutdoorType.Outdoor, false, false)]
    [InlineData(IndoorOutdoorType.Outdoor, true, true)]
    public async Task SevereWeather_WhenTheStopCannotBeChanged_HasNoProposal(
        IndoorOutdoorType kind, bool reviewed, bool mandatory)
    {
        await using var kit = await ReroutingTestKit.CreateAsync();
        var (a, b, c) = await ThreeStopsAsync(kit, bIndoorOutdoor: kind, reviewed: reviewed, mandatoryB: mandatory);
        var session = await kit.StartSessionAsync(a, b, c);
        var incident = await kit.AddIncidentAsync(session, Incident.SevereWeatherType, kit.ItemIdOf(session, b));

        (await Builder(kit).BuildAsync(incident.Id, Now, CancellationToken.None)).Should().BeNull();
    }

    [Fact]
    public async Task SevereWeather_UsesTheConfiguredReroutingRadius()
    {
        await using var kit = await ReroutingTestKit.CreateAsync();
        var (a, b, c) = await ThreeStopsAsync(kit, bIndoorOutdoor: IndoorOutdoorType.Outdoor, reviewed: true);
        _ = await kit.AddPoiAsync("Indoor 2 km away", ReroutingTestKit.NorthByKm(2.1), "Museum", IndoorOutdoorType.Indoor, reviewed: true);
        kit.Db.SystemConfigs.Add(new SystemConfig("Rerouting.SearchRadiusKm", "1", null, null, Now));
        await kit.Db.SaveChangesAsync();
        var session = await kit.StartSessionAsync(a, b, c);
        var incident = await kit.AddIncidentAsync(session, Incident.SevereWeatherType, kit.ItemIdOf(session, b));

        var snapshot = await Builder(kit).BuildAsync(incident.Id, Now, CancellationToken.None);

        snapshot!.Strategy.Should().Be(ReroutingStrategies.Drop, "the only indoor POI is outside the configured 1 km radius");
    }

    [Fact]
    public async Task PoiClosure_ReplacesWithASameCategoryPoi()
    {
        await using var kit = await ReroutingTestKit.CreateAsync();
        var (a, b, c) = await ThreeStopsAsync(kit);
        var sameCategory = await kit.AddPoiAsync("Another museum", ReroutingTestKit.NorthByKm(0.4), "Culture");
        var session = await kit.StartSessionAsync(a, b, c);
        var incident = await kit.AddIncidentAsync(session, Incident.PoiClosureType, kit.ItemIdOf(session, b));

        var snapshot = await Builder(kit).BuildAsync(incident.Id, Now, CancellationToken.None);

        snapshot!.Strategy.Should().Be(ReroutingStrategies.ReplaceSameCategory);
        snapshot.ReasonCode.Should().Be("trip_alert.poi_closure");
        snapshot.ProposedStops.Select(stop => stop.PoiId).Should().Equal(a.Id, sameCategory.Id, c.Id);
    }

    [Fact]
    public async Task PoiClosure_WithoutAReplacement_PostponesTheStopToWhenItOpens()
    {
        await using var kit = await ReroutingTestKit.CreateAsync();
        var a = await kit.AddPoiAsync("Stop A", ReroutingTestKit.NorthByKm(0));
        var b = await kit.AddPoiAsync("Stop B", ReroutingTestKit.NorthByKm(0.1), opensAt: new TimeOnly(11, 45), closesAt: new TimeOnly(20, 0));
        var c = await kit.AddPoiAsync("Stop C", ReroutingTestKit.NorthByKm(0.2));
        await kit.AddRequestAsync([b.Id]);
        var session = await kit.StartSessionAsync(a, b, c);
        var incident = await kit.AddIncidentAsync(session, Incident.PoiClosureType, kit.ItemIdOf(session, b));

        var snapshot = await Builder(kit).BuildAsync(incident.Id, Now, CancellationToken.None);

        snapshot.Should().NotBeNull("a mandatory stop is never dropped, but it can be visited later");
        snapshot!.Strategy.Should().Be(ReroutingStrategies.Postpone);
        snapshot.ProposedStops.Select(stop => stop.PoiId).Should().Equal(a.Id, c.Id, b.Id);
        snapshot.ProposedStops.Single(stop => stop.PoiId == b.Id).Change.Should().Be(ReroutingStopChanges.Moved);
        snapshot.ProposedStops.Where(stop => stop.PoiId != b.Id).Should().OnlyContain(stop => stop.Change == ReroutingStopChanges.Kept);
        snapshot.ProposedStops.Single(stop => stop.PoiId == b.Id).IsMandatory.Should().BeTrue();
        snapshot.RemovedPoiIds.Should().BeEmpty();
    }

    [Fact]
    public async Task PoiClosure_ForAMandatoryStopThatNeverOpensToday_HasNoProposal()
    {
        await using var kit = await ReroutingTestKit.CreateAsync();
        var a = await kit.AddPoiAsync("Stop A", ReroutingTestKit.NorthByKm(0));
        var b = await kit.AddPoiAsync("Stop B", ReroutingTestKit.NorthByKm(0.1), opensAt: new TimeOnly(19, 0), closesAt: new TimeOnly(20, 0));
        await kit.AddRequestAsync([b.Id]);
        var session = await kit.StartSessionAsync(a, b);
        var incident = await kit.AddIncidentAsync(session, Incident.PoiClosureType, kit.ItemIdOf(session, b));

        (await Builder(kit).BuildAsync(incident.Id, Now, CancellationToken.None)).Should().BeNull();
    }

    [Fact]
    public async Task ScheduleDelay_WhenTheCurrentOrderStillFits_HasNothingToFix()
    {
        await using var kit = await ReroutingTestKit.CreateAsync();
        var (a, b, c) = await ThreeStopsAsync(kit);
        var session = await kit.StartSessionAsync(a, b, c);
        var incident = await kit.AddIncidentAsync(session, Incident.ScheduleDelayType, kit.ItemIdOf(session, a));

        (await Builder(kit).BuildAsync(incident.Id, Now, CancellationToken.None)).Should().BeNull();
    }

    [Fact]
    public async Task ScheduleDelay_WhenTheDayNoLongerFits_DropsTheLeastPreferredStop()
    {
        await using var kit = await ReroutingTestKit.CreateAsync();
        var stops = new List<PointOfInterest>();
        for (var index = 0; index < 4; index++)
        {
            stops.Add(await kit.AddPoiAsync($"Stop {index + 1}", ReroutingTestKit.NorthByKm(index * 0.1)));
        }

        await kit.AddRequestAsync(availableMinutes: 300);
        var session = await kit.StartSessionAsync(stops.ToArray());
        var incident = await kit.AddIncidentAsync(session, Incident.ScheduleDelayType, kit.ItemIdOf(session, stops[0]));

        var snapshot = await Builder(kit).BuildAsync(incident.Id, Now, CancellationToken.None);

        snapshot.Should().NotBeNull();
        snapshot!.Strategy.Should().Be(ReroutingStrategies.ReduceStops);
        snapshot.ReasonCode.Should().Be("trip_alert.schedule_delay");
        snapshot.RemovedPoiIds.Should().HaveCount(1);
        snapshot.ProposedStops.Should().HaveCount(3);
        snapshot.ProposedStops.Should().OnlyContain(stop => stop.Change == ReroutingStopChanges.Kept);
    }

    [Fact]
    public async Task RouteDeviation_ProposesAShorterOrder()
    {
        await using var kit = await ReroutingTestKit.CreateAsync();
        var far = await kit.AddPoiAsync("Far", ReroutingTestKit.NorthByKm(3));
        var near = await kit.AddPoiAsync("Near", ReroutingTestKit.NorthByKm(0.5));
        var mid = await kit.AddPoiAsync("Mid", ReroutingTestKit.NorthByKm(1.5));
        await kit.AddRequestAsync();
        var session = await kit.StartSessionAsync(far, near, mid);
        var incident = await kit.AddIncidentAsync(session, Incident.RouteDeviationType, kit.ItemIdOf(session, far));

        var snapshot = await Builder(kit).BuildAsync(incident.Id, Now, CancellationToken.None);

        snapshot.Should().NotBeNull();
        snapshot!.Strategy.Should().Be(ReroutingStrategies.Reorder);
        snapshot.ReasonCode.Should().Be("trip_alert.route_deviation");
        snapshot.ProposedStops.Select(stop => stop.PoiId).Should().Equal(near.Id, mid.Id, far.Id);
        snapshot.RemovedPoiIds.Should().BeEmpty();
        snapshot.ProposedStops.Should().Contain(stop => stop.Change == ReroutingStopChanges.Moved);
    }

    [Fact]
    public async Task RouteDeviation_WhenTheOrderIsAlreadyTheShortestAndFits_HasNoProposal()
    {
        await using var kit = await ReroutingTestKit.CreateAsync();
        var near = await kit.AddPoiAsync("Near", ReroutingTestKit.NorthByKm(0.5));
        var mid = await kit.AddPoiAsync("Mid", ReroutingTestKit.NorthByKm(1.5));
        var far = await kit.AddPoiAsync("Far", ReroutingTestKit.NorthByKm(3));
        await kit.AddRequestAsync();
        var session = await kit.StartSessionAsync(near, mid, far);
        var incident = await kit.AddIncidentAsync(session, Incident.RouteDeviationType, kit.ItemIdOf(session, near));

        (await Builder(kit).BuildAsync(incident.Id, Now, CancellationToken.None)).Should().BeNull();
    }

    [Fact]
    public async Task WhenTheAffectedStopIsNotPendingAnyMore_HasNoProposal()
    {
        await using var kit = await ReroutingTestKit.CreateAsync();
        var (a, b, c) = await ThreeStopsAsync(kit, bIndoorOutdoor: IndoorOutdoorType.Outdoor, reviewed: true);
        var session = await kit.StartSessionAsync(a, b, c);
        var incident = await kit.AddIncidentAsync(session, Incident.SevereWeatherType, kit.ItemIdOf(session, b));
        session.ReachItem(kit.ItemIdOf(session, b), Now.AddMinutes(1));
        await kit.Db.SaveChangesAsync();

        (await Builder(kit).BuildAsync(incident.Id, Now, CancellationToken.None)).Should().BeNull();
    }

    [Fact]
    public async Task WeatherAdvisoryIncidents_NeverGetAProposal()
    {
        await using var kit = await ReroutingTestKit.CreateAsync();
        var (a, b, c) = await ThreeStopsAsync(kit, bIndoorOutdoor: IndoorOutdoorType.Outdoor, reviewed: true);
        var session = await kit.StartSessionAsync(a, b, c);
        var incident = await kit.AddIncidentAsync(session, Incident.WeatherAdvisoryType, kit.ItemIdOf(session, b));

        (await Builder(kit).BuildAsync(incident.Id, Now, CancellationToken.None)).Should().BeNull();
    }

    [Fact]
    public async Task WhenNoTimeIsLeftInTheDay_HasNoProposal()
    {
        await using var kit = await ReroutingTestKit.CreateAsync();
        var (a, b, c) = await ThreeStopsAsync(kit, bIndoorOutdoor: IndoorOutdoorType.Outdoor, reviewed: true);
        var session = await kit.StartSessionAsync(a, b, c);
        var incident = await kit.AddIncidentAsync(session, Incident.SevereWeatherType, kit.ItemIdOf(session, b));

        var snapshot = await Builder(kit).BuildAsync(incident.Id, ReroutingTestKit.RequestStart.AddHours(9), CancellationToken.None);

        snapshot.Should().BeNull();
    }

    [Fact]
    public async Task UnknownIncident_HasNoProposal()
    {
        await using var kit = await ReroutingTestKit.CreateAsync();
        await kit.AddRequestAsync();

        (await Builder(kit).BuildAsync(9999, Now, CancellationToken.None)).Should().BeNull();
    }

    private static ReroutingProposalBuilder Builder(ReroutingTestKit kit) =>
        new(kit.Db, kit.Service, Options.Create(new ReroutingOptions()));

    private static async Task<(PointOfInterest A, PointOfInterest B, PointOfInterest C)> ThreeStopsAsync(
        ReroutingTestKit kit,
        IndoorOutdoorType bIndoorOutdoor = IndoorOutdoorType.Outdoor,
        bool reviewed = false,
        bool mandatoryB = false)
    {
        var a = await kit.AddPoiAsync("Stop A", ReroutingTestKit.NorthByKm(0));
        var b = await kit.AddPoiAsync("Stop B", ReroutingTestKit.NorthByKm(0.1), "Culture", bIndoorOutdoor, reviewed);
        var c = await kit.AddPoiAsync("Stop C", ReroutingTestKit.NorthByKm(0.2));
        await kit.AddRequestAsync(mandatoryB ? [b.Id] : []);
        return (a, b, c);
    }
}