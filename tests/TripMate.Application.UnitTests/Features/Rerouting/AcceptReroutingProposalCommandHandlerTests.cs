using FluentAssertions;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

using TripMate.Application.Features.Itineraries.Common;
using TripMate.Application.Features.Rerouting.Accept;
using TripMate.Application.Features.Rerouting.Common;
using TripMate.Application.UnitTests.Features.Rerouting.Engine;
using TripMate.Application.UnitTests.TestUtilities;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Application.UnitTests.Features.Rerouting;

/// <summary>UC-15 T7 (logic): the accept pipeline against in-memory data. Foreign keys and the real save order are proven in the SQL Server suite.</summary>
public sealed class AcceptReroutingProposalCommandHandlerTests
{
    private static readonly DateTimeOffset ProposedAt = ReroutingTestKit.RequestStart.AddMinutes(30);
    private static readonly DateTimeOffset AcceptedAt = ProposedAt.AddMinutes(5);

    [Fact]
    public async Task Accept_CreatesOneNewVersionKeepsTheOldOneAndMovesTheSession()
    {
        await using var kit = await ReroutingTestKit.CreateAsync();
        var scenario = await DropScenarioAsync(kit);
        var locks = new RecordingLock();

        var result = await Handler(kit, AcceptedAt, locks).Handle(Command(scenario), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        var response = result.Value;
        response.ItineraryId.Should().NotBe(scenario.OldItineraryId);
        response.ItineraryVersion.Should().Be(2);
        response.State.Should().Be(TripSession.NavigatingState);
        response.Items.OrderBy(item => item.SequenceNo).Select(item => item.PoiId)
            .Should().Equal(scenario.A.Id, scenario.C.Id);
        response.Items.Should().OnlyContain(item => item.Status == TripSessionItem.PendingStatus);
        locks.Acquired.Should().Equal(kit.Request.Id);

        var versions = await kit.Db.Itineraries.AsNoTracking().Include(itinerary => itinerary.Items)
            .Where(itinerary => itinerary.SchedulingRequestId == kit.Request.Id).OrderBy(itinerary => itinerary.Version).ToArrayAsync();
        versions.Should().HaveCount(2);
        versions[0].Id.Should().Be(scenario.OldItineraryId);
        versions[0].Status.Should().Be(Itinerary.ActiveStatus);
        versions[0].Items.Should().HaveCount(3, "the previous version is retained, never overwritten (BR-27)");
        versions[1].Id.Should().Be(response.ItineraryId);
        versions[1].Status.Should().Be(Itinerary.ActiveStatus);
        versions[1].SolverOutcome.Should().Be(RerouteProvenance.SolverOutcome);
        versions[1].Items.OrderBy(item => item.SequenceNo).Where(item => item.Kind == ItineraryItemKind.Visit).Select(item => item.PointOfInterestId)
            .Should().Equal(scenario.A.Id, scenario.C.Id);

        var session = await kit.Db.TripSessions.AsNoTracking().Include(candidate => candidate.StateHistory)
            .SingleAsync(candidate => candidate.Id == scenario.Session.Id);
        session.ItineraryId.Should().Be(response.ItineraryId);
        session.RequestedItineraryId.Should().Be(scenario.OldItineraryId);
        session.StateHistory.Count(history => history.Reason == TripStateHistory.RerouteAcceptedReason).Should().Be(1);

        var proposal = await kit.Db.ReroutingEvents.AsNoTracking().SingleAsync();
        proposal.Status.Should().Be(ReroutingEvent.StatusAccepted);
        proposal.DecidedAtUtc.Should().Be(AcceptedAt);
    }

    [Fact]
    public async Task Accept_KeepsReachedStopsAsHistoryAndRenumbersTheRest()
    {
        await using var kit = await ReroutingTestKit.CreateAsync();
        var scenario = await FourStopScenarioAsync(kit);

        var result = await Handler(kit, AcceptedAt).Handle(Command(scenario), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        var rows = await kit.Db.TripSessions.AsNoTracking().Where(session => session.Id == scenario.Session.Id)
            .SelectMany(session => session.Items).OrderBy(item => item.SequenceNo).ToArrayAsync();
        rows.Select(row => row.PoiId).Should().Equal(scenario.A.Id, scenario.B.Id, scenario.D.Id);
        rows[0].Status.Should().Be(TripSessionItem.ReachedStatus);
        rows[0].ItineraryItemId.Should().Be(scenario.ReachedItemId, "the reached row is untouched history");
        rows[1].ItineraryItemId.Should().NotBe(scenario.PendingItemIds[0]);
        rows.Select(row => row.SequenceNo).Should().OnlyHaveUniqueItems();

        var successor = await kit.Db.Itineraries.AsNoTracking().Include(itinerary => itinerary.Items)
            .SingleAsync(itinerary => itinerary.Id == result.Value.ItineraryId);
        var ordered = successor.Items.OrderBy(item => item.SequenceNo).ToArray();
        ordered[0].PointOfInterestId.Should().Be(scenario.A.Id);
        ordered[0].SequenceNo.Should().Be(rows[0].SequenceNo);
        ordered.Select(item => item.SequenceNo).Should().OnlyHaveUniqueItems();
        ordered.Where(item => item.Kind == ItineraryItemKind.Visit).Select(item => item.PointOfInterestId)
            .Should().Equal(scenario.A.Id, scenario.B.Id, scenario.D.Id);
        rows.Skip(1).Select(row => row.ItineraryItemId).Should().BeSubsetOf(successor.Items.Select(item => item.Id));
        rows[0].SequenceNo.Should().BeLessThan(rows[1].SequenceNo);
    }

    [Fact]
    public async Task Accept_ResolvesIncidentsAndRepointsOrDetachesThem()
    {
        await using var kit = await ReroutingTestKit.CreateAsync();
        var scenario = await DropScenarioAsync(kit);
        var onKeptStop = await kit.AddIncidentAsync(
            scenario.Session, Incident.ScheduleDelayType, kit.ItemIdOf(scenario.Session, scenario.C));

        await Handler(kit, AcceptedAt).Handle(Command(scenario), CancellationToken.None);

        var trigger = await kit.Db.Incidents.AsNoTracking().SingleAsync(incident => incident.Id == scenario.IncidentId);
        trigger.ResolvedAtUtc.Should().Be(AcceptedAt);
        trigger.AffectedItineraryItemId.Should().BeNull("its stop left the plan");
        var kept = await kit.Db.Incidents.AsNoTracking().SingleAsync(incident => incident.Id == onKeptStop.Id);
        kept.ResolvedAtUtc.Should().Be(AcceptedAt);
        var newRowForC = await kit.Db.TripSessions.AsNoTracking().Where(session => session.Id == scenario.Session.Id)
            .SelectMany(session => session.Items).SingleAsync(row => row.PoiId == scenario.C.Id);
        kept.AffectedItineraryItemId.Should().Be(newRowForC.ItineraryItemId, "the same POI is still in the plan");
    }

    [Fact]
    public async Task Accept_LeavesAnIncidentOnAReachedStopAlone()
    {
        await using var kit = await ReroutingTestKit.CreateAsync();
        var scenario = await FourStopScenarioAsync(kit);
        var overstay = await kit.AddIncidentAsync(
            scenario.Session, Incident.ScheduleDelayType, scenario.ReachedItemId, "overstay:" + scenario.ReachedItemId);

        await Handler(kit, AcceptedAt).Handle(Command(scenario), CancellationToken.None);

        var stored = await kit.Db.Incidents.AsNoTracking().SingleAsync(incident => incident.Id == overstay.Id);
        stored.ResolvedAtUtc.Should().BeNull();
        stored.AffectedItineraryItemId.Should().Be(scenario.ReachedItemId);
    }

    [Fact]
    public async Task Accept_ClosesEveryOtherOpenProposalOfTheSession()
    {
        await using var kit = await ReroutingTestKit.CreateAsync();
        var scenario = await DropScenarioAsync(kit);
        var other = await kit.AddIncidentAsync(scenario.Session, Incident.PoiClosureType, kit.ItemIdOf(scenario.Session, scenario.C));
        var sibling = ReroutingEvent.Propose(other, scenario.Session, "{\"schemaVersion\":1}", ProposedAt);
        kit.Db.ReroutingEvents.Add(sibling);
        await kit.Db.SaveChangesAsync();

        await Handler(kit, AcceptedAt).Handle(Command(scenario), CancellationToken.None);

        var stored = await kit.Db.ReroutingEvents.AsNoTracking().SingleAsync(proposal => proposal.Id == sibling.Id);
        stored.Status.Should().Be(ReroutingEvent.StatusExpired);
        stored.DecidedAtUtc.Should().Be(AcceptedAt);
    }

    [Fact]
    public async Task Accept_Twice_ReplaysTheSuccessWithoutAnotherVersion()
    {
        await using var kit = await ReroutingTestKit.CreateAsync();
        var scenario = await DropScenarioAsync(kit);
        var first = await Handler(kit, AcceptedAt).Handle(Command(scenario), CancellationToken.None);

        var replay = await Handler(kit, AcceptedAt.AddSeconds(2)).Handle(Command(scenario), CancellationToken.None);

        replay.IsSuccess.Should().BeTrue();
        replay.Value.ItineraryId.Should().Be(first.Value.ItineraryId);
        replay.Value.ItineraryVersion.Should().Be(2);
        (await kit.Db.Itineraries.CountAsync(itinerary => itinerary.SchedulingRequestId == kit.Request.Id)).Should().Be(2);
        (await kit.Db.TripStateHistories.CountAsync(history => history.Reason == TripStateHistory.RerouteAcceptedReason)).Should().Be(1);
        (await kit.Db.ReroutingEvents.AsNoTracking().SingleAsync()).DecidedAtUtc.Should().Be(AcceptedAt, "a replay never moves the decision time");
    }

    [Fact]
    public async Task Accept_AfterTheProposalWasRejectedOrExpired_IsRefusedWithoutChange()
    {
        await using var kit = await ReroutingTestKit.CreateAsync();
        var rejected = await DropScenarioAsync(kit);
        (await kit.Db.ReroutingEvents.SingleAsync()).Reject(ProposedAt.AddMinutes(1));
        await kit.Db.SaveChangesAsync();

        var afterReject = await Handler(kit, AcceptedAt).Handle(Command(rejected), CancellationToken.None);

        afterReject.ErrorCode.Should().Be(ReroutingErrorCodes.ProposalClosed);
        (await kit.Db.ReroutingEvents.AsNoTracking().SingleAsync()).Status.Should().Be(ReroutingEvent.StatusRejected);
        await AssertUntouchedAsync(kit, rejected);

    }

    [Fact]
    public async Task Accept_OfAnExpiredProposal_IsRefusedWithoutChange()
    {
        await using var kit = await ReroutingTestKit.CreateAsync();
        var scenario = await DropScenarioAsync(kit);
        (await kit.Db.ReroutingEvents.SingleAsync()).Expire(ProposedAt.AddMinutes(20));
        await kit.Db.SaveChangesAsync();

        var result = await Handler(kit, AcceptedAt).Handle(Command(scenario), CancellationToken.None);

        result.ErrorCode.Should().Be(ReroutingErrorCodes.ProposalExpired);
        await AssertUntouchedAsync(kit, scenario);
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(1, false)]
    public async Task Accept_ChecksTheTtlAtTheMomentOfTheCall(int extraMilliseconds, bool accepted)
    {
        await using var kit = await ReroutingTestKit.CreateAsync();
        var scenario = await DropScenarioAsync(kit);
        var now = ProposedAt.AddMinutes(15).AddMilliseconds(extraMilliseconds);

        var result = await Handler(kit, now).Handle(Command(scenario), CancellationToken.None);

        result.IsSuccess.Should().Be(accepted);
        if (!accepted)
        {
            result.ErrorCode.Should().Be(ReroutingErrorCodes.ProposalExpired);
            var stored = await kit.Db.ReroutingEvents.AsNoTracking().SingleAsync();
            stored.Status.Should().Be(ReroutingEvent.StatusExpired, "the expiry is written even though no sweep has run");
            stored.DecidedAtUtc.Should().Be(now);
            await AssertUntouchedAsync(kit, scenario);
        }
    }

    [Fact]
    public async Task Accept_WhenTheSessionHasEnded_ExpiresTheProposal()
    {
        await using var kit = await ReroutingTestKit.CreateAsync();
        var scenario = await DropScenarioAsync(kit);
        (await kit.Db.TripSessions.SingleAsync()).Finish(ProposedAt.AddMinutes(1));
        await kit.Db.SaveChangesAsync();

        var result = await Handler(kit, AcceptedAt).Handle(Command(scenario), CancellationToken.None);

        result.ErrorCode.Should().Be(ReroutingErrorCodes.ProposalExpired);
        (await kit.Db.ReroutingEvents.AsNoTracking().SingleAsync()).Status.Should().Be(ReroutingEvent.StatusExpired);
        (await kit.Db.Itineraries.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task Accept_WhenABaseStopWasReachedMeanwhile_ExpiresTheProposal()
    {
        await using var kit = await ReroutingTestKit.CreateAsync();
        var scenario = await DropScenarioAsync(kit);
        var session = await kit.Db.TripSessions.Include(candidate => candidate.Items).SingleAsync();
        session.ReachItem(kit.ItemIdOf(session, scenario.A), ProposedAt.AddMinutes(1));
        await kit.Db.SaveChangesAsync();

        var result = await Handler(kit, AcceptedAt).Handle(Command(scenario), CancellationToken.None);

        result.ErrorCode.Should().Be(ReroutingErrorCodes.ProposalExpired);
        (await kit.Db.ReroutingEvents.AsNoTracking().SingleAsync()).Status.Should().Be(ReroutingEvent.StatusExpired);
        (await kit.Db.Itineraries.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task Accept_WhenTheProposedPlanIsNoLongerFeasible_ExpiresTheProposalAndChangesNothing()
    {
        await using var kit = await ReroutingTestKit.CreateAsync();
        var a = await kit.AddPoiAsync("Stop A", ReroutingTestKit.NorthByKm(0));
        var b = await kit.AddPoiAsync("Stop B", ReroutingTestKit.NorthByKm(0.1), "Culture", IndoorOutdoorType.Outdoor, reviewed: true);
        var c = await kit.AddPoiAsync("Stop C", ReroutingTestKit.NorthByKm(0.2));
        var indoor = await kit.AddPoiAsync("Cham Museum", ReroutingTestKit.NorthByKm(0.3), "Museum", IndoorOutdoorType.Indoor, reviewed: true);
        await kit.AddRequestAsync();
        var session = await kit.StartSessionAsync(a, b, c);
        var scenario = await ProposeAsync(kit, session, [a, b, c], affectedIndex: 1);
        (await kit.Db.ReroutingEvents.SingleAsync()).ProposedItinerarySnapshot
            .Should().Contain("ReplaceWithIndoor");
        (await kit.Db.PointsOfInterest.SingleAsync(poi => poi.Id == indoor.Id)).TryDeactivate(ProposedAt.AddMinutes(1));
        await kit.Db.SaveChangesAsync();

        var result = await Handler(kit, AcceptedAt).Handle(Command(scenario), CancellationToken.None);

        result.ErrorCode.Should().Be(ReroutingErrorCodes.ProposalInfeasible);
        (await kit.Db.ReroutingEvents.AsNoTracking().SingleAsync()).Status.Should().Be(ReroutingEvent.StatusExpired);
        await AssertUntouchedAsync(kit, scenario);
    }

    [Fact]
    public async Task Accept_ByAnotherTravelerOrForAnotherSessionOrADemoRow_IsNotFound()
    {
        await using var kit = await ReroutingTestKit.CreateAsync();
        var scenario = await DropScenarioAsync(kit);
        var demo = ReroutingEvent.Propose(
            await kit.Db.Incidents.SingleAsync(), scenario.Session, "{\"demo\":true,\"route\":\"bridge detour\"}", ProposedAt);
        kit.Db.ReroutingEvents.Add(demo);
        await kit.Db.SaveChangesAsync();

        foreach (var wrong in new[]
        {
            Command(scenario) with { TravelerUserId = ReroutingTestKit.TravelerId + 1 },
            Command(scenario) with { SessionId = scenario.Session.Id + 50 },
            Command(scenario) with { ProposalId = scenario.ProposalId + 50 },
            Command(scenario) with { ProposalId = demo.Id },
        })
        {
            var result = await Handler(kit, AcceptedAt).Handle(wrong, CancellationToken.None);
            result.ErrorCode.Should().Be(ReroutingErrorCodes.NotFound);
        }

        await AssertUntouchedAsync(kit, scenario);
    }

    // ---------------------------------------------------------------- helpers

    private static AcceptReroutingProposalCommandHandler Handler(
        ReroutingTestKit kit,
        DateTimeOffset now,
        RecordingLock? locks = null) =>
        new(
            kit.Db,
            new FakeDateTimeProvider { UtcNow = now },
            Options.Create(new ReroutingOptions()),
            kit.Service,
            new ItineraryAccessService(kit.Db),
            locks ?? new RecordingLock());

    private static AcceptReroutingProposalCommand Command(Scenario scenario) =>
        new(scenario.Session.Id, scenario.ProposalId, ReroutingTestKit.TravelerId);

    private static async Task AssertUntouchedAsync(ReroutingTestKit kit, Scenario scenario)
    {
        (await kit.Db.Itineraries.CountAsync(itinerary => itinerary.SchedulingRequestId == kit.Request.Id)).Should().Be(1);
        var session = await kit.Db.TripSessions.AsNoTracking().Include(candidate => candidate.Items)
            .SingleAsync(candidate => candidate.Id == scenario.Session.Id);
        session.ItineraryId.Should().Be(scenario.OldItineraryId);
        session.Items.Select(item => item.ItineraryItemId).Should().BeEquivalentTo(scenario.AllItemIds);
        (await kit.Db.TripStateHistories.CountAsync(history => history.Reason == TripStateHistory.RerouteAcceptedReason)).Should().Be(0);
    }

    /// <summary>[A, B, C] with a Drop proposal for B after a severe-weather incident.</summary>
    private static async Task<Scenario> DropScenarioAsync(ReroutingTestKit kit)
    {
        var a = await kit.AddPoiAsync("Stop A", ReroutingTestKit.NorthByKm(0));
        var b = await kit.AddPoiAsync("Stop B", ReroutingTestKit.NorthByKm(0.1), "Culture", IndoorOutdoorType.Outdoor, reviewed: true);
        var c = await kit.AddPoiAsync("Stop C", ReroutingTestKit.NorthByKm(0.2));
        await kit.AddRequestAsync();
        var session = await kit.StartSessionAsync(a, b, c);
        return await ProposeAsync(kit, session, [a, b, c], affectedIndex: 1);
    }

    /// <summary>[A, B, C, D]: A reached and left, then a severe-weather Drop proposal for C (proposed [B, D]).</summary>
    private static async Task<Scenario> FourStopScenarioAsync(ReroutingTestKit kit)
    {
        var a = await kit.AddPoiAsync("Stop A", ReroutingTestKit.NorthByKm(0));
        var b = await kit.AddPoiAsync("Stop B", ReroutingTestKit.NorthByKm(0.1));
        var c = await kit.AddPoiAsync("Stop C", ReroutingTestKit.NorthByKm(0.2), "Culture", IndoorOutdoorType.Outdoor, reviewed: true);
        var d = await kit.AddPoiAsync("Stop D", ReroutingTestKit.NorthByKm(0.3));
        await kit.AddRequestAsync();
        var session = await kit.StartSessionAsync(a, b, c, d);
        session.ReachItem(kit.ItemIdOf(session, a), ReroutingTestKit.RequestStart.AddMinutes(15));
        session.Depart(ReroutingTestKit.RequestStart.AddMinutes(20));
        await kit.Db.SaveChangesAsync();
        return await ProposeAsync(kit, session, [a, b, c, d], affectedIndex: 2);
    }

    private static async Task<Scenario> ProposeAsync(
        ReroutingTestKit kit,
        TripSession session,
        PointOfInterest[] stops,
        int affectedIndex)
    {
        var incident = await kit.AddIncidentAsync(
            session, Incident.SevereWeatherType, kit.ItemIdOf(session, stops[affectedIndex]));
        (await ReroutingProposalGeneratorTests.Generator(kit).TryGenerateAsync(incident.Id, ProposedAt, CancellationToken.None))
            .Should().BeTrue();
        var proposal = await kit.Db.ReroutingEvents.OrderByDescending(candidate => candidate.Id).FirstAsync();
        var items = session.Items.OrderBy(item => item.SequenceNo).ToArray();
        return new Scenario(
            session,
            proposal.Id,
            incident.Id,
            session.ItineraryId,
            stops,
            items.FirstOrDefault(item => item.Status == TripSessionItem.ReachedStatus)?.ItineraryItemId ?? 0,
            items.Where(item => item.Status == TripSessionItem.PendingStatus).Select(item => item.ItineraryItemId).ToArray(),
            items.Select(item => item.ItineraryItemId).ToArray());
    }

    private sealed record Scenario(
        TripSession Session,
        long ProposalId,
        long IncidentId,
        long OldItineraryId,
        PointOfInterest[] Stops,
        long ReachedItemId,
        long[] PendingItemIds,
        long[] AllItemIds)
    {
        public PointOfInterest A => Stops[0];

        public PointOfInterest B => Stops[1];

        public PointOfInterest C => Stops[2];

        public PointOfInterest D => Stops[3];
    }

    private sealed class RecordingLock : IItineraryMutationLock
    {
        public List<long> Acquired { get; } = [];

        public Task AcquireAsync(long mutationResourceId, CancellationToken cancellationToken)
        {
            Acquired.Add(mutationResourceId);
            return Task.CompletedTask;
        }
    }
}