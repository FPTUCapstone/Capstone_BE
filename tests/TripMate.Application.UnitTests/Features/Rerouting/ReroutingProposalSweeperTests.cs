using FluentAssertions;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

using TripMate.Application.Features.Rerouting.Common;
using TripMate.Application.Features.Rerouting.Generate;
using TripMate.Application.UnitTests.Features.Rerouting.Engine;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Application.UnitTests.Features.Rerouting;

public sealed class ReroutingProposalSweeperTests
{
    private static readonly DateTimeOffset Now = ReroutingTestKit.RequestStart.AddMinutes(30);

    [Fact]
    public async Task Sweep_ExpiresOnlyProposalsPastTheirTtl()
    {
        await using var kit = await ReroutingTestKit.CreateAsync();
        var (session, stops) = await SessionAsync(kit);
        var old = await AddEventAsync(kit, session, stops[0], Now.AddMinutes(-16));
        var edge = await AddEventAsync(kit, session, stops[1], Now.AddMinutes(-15));
        var fresh = await AddEventAsync(kit, session, stops[2], Now.AddMinutes(-1));

        await Sweeper(kit, new SpyGenerator()).SweepAsync(Now, CancellationToken.None);

        (await Reload(kit, old)).Status.Should().Be(ReroutingEvent.StatusExpired);
        (await Reload(kit, old)).DecidedAtUtc.Should().Be(Now);
        (await Reload(kit, edge)).Status.Should().Be(ReroutingEvent.StatusProposed, "exactly at the TTL is still open");
        (await Reload(kit, fresh)).Status.Should().Be(ReroutingEvent.StatusProposed);
    }

    [Fact]
    public async Task Sweep_LeavesAcceptedAndRejectedProposalsAlone()
    {
        await using var kit = await ReroutingTestKit.CreateAsync();
        var (session, stops) = await SessionAsync(kit);
        var accepted = await AddEventAsync(kit, session, stops[0], Now.AddHours(-1));
        var rejected = await AddEventAsync(kit, session, stops[1], Now.AddHours(-1));
        accepted.Accept(Now.AddMinutes(-50));
        rejected.Reject(Now.AddMinutes(-50));
        await kit.Db.SaveChangesAsync();

        await Sweeper(kit, new SpyGenerator()).SweepAsync(Now, CancellationToken.None);

        (await Reload(kit, accepted)).Status.Should().Be(ReroutingEvent.StatusAccepted);
        (await Reload(kit, rejected)).Status.Should().Be(ReroutingEvent.StatusRejected);
    }

    [Fact]
    public async Task Sweep_ExpiresAFreshProposalWhenItsSessionHasEnded()
    {
        await using var kit = await ReroutingTestKit.CreateAsync();
        var (session, stops) = await SessionAsync(kit);
        var fresh = await AddEventAsync(kit, session, stops[0], Now.AddMinutes(-1));
        session.Finish(Now.AddSeconds(-30));
        await kit.Db.SaveChangesAsync();

        await Sweeper(kit, new SpyGenerator()).SweepAsync(Now, CancellationToken.None);

        (await Reload(kit, fresh)).Status.Should().Be(ReroutingEvent.StatusExpired);
    }

    [Fact]
    public async Task Sweep_RetriesRecentTriggerIncidentsThatStillHaveNoProposal()
    {
        await using var kit = await ReroutingTestKit.CreateAsync();
        var (session, stops) = await SessionAsync(kit);
        var recent = await kit.AddIncidentAsync(
            session, Incident.PoiClosureType, kit.ItemIdOf(session, stops[0]), detectedAt: Now.AddMinutes(-30));
        var spy = new SpyGenerator();

        await Sweeper(kit, spy).SweepAsync(Now, CancellationToken.None);

        spy.IncidentIds.Should().ContainSingle().Which.Should().Be(recent.Id, "exactly at the window edge still counts");
        spy.Times.Should().OnlyContain(time => time == Now);
    }

    [Fact]
    public async Task Sweep_DoesNotRetryOutsideTheWindowResolvedOrAlreadyProposedOrExcludedIncidents()
    {
        await using var kit = await ReroutingTestKit.CreateAsync();
        var (session, stops) = await SessionAsync(kit);
        var itemId = kit.ItemIdOf(session, stops[0]);
        var recentTime = Now.AddMinutes(-5);
        _ = await kit.AddIncidentAsync(session, Incident.PoiClosureType, itemId, detectedAt: Now.AddMinutes(-31));
        var resolved = await kit.AddIncidentAsync(session, Incident.SevereWeatherType, itemId, detectedAt: recentTime);
        resolved.Resolve(Now);
        var hasEvent = await kit.AddIncidentAsync(session, Incident.RouteDeviationType, itemId, detectedAt: recentTime);
        await AddEventAsync(kit, session, stops[1], Now, hasEvent);
        _ = await kit.AddIncidentAsync(session, Incident.WeatherAdvisoryType, itemId, detectedAt: recentTime);
        _ = await kit.AddIncidentAsync(
            session,
            Incident.ScheduleDelayType,
            itemId,
            $"{TripMate.Application.Features.TripAlerts.Common.TripAlertResponse.OverstaySourcePrefix}{itemId}",
            recentTime);
        await kit.Db.SaveChangesAsync();
        var spy = new SpyGenerator();

        await Sweeper(kit, spy).SweepAsync(Now, CancellationToken.None);

        spy.IncidentIds.Should().BeEmpty("too old, resolved, already proposed, advisory and overstay incidents are never retried");
    }

    [Fact]
    public async Task Sweep_RetriesAtMostTwentyIncidentsPerScan_OldestFirst()
    {
        await using var kit = await ReroutingTestKit.CreateAsync();
        var (session, stops) = await SessionAsync(kit);
        var itemId = kit.ItemIdOf(session, stops[0]);
        var ids = new List<long>();
        for (var index = 0; index < 25; index++)
        {
            ids.Add((await kit.AddIncidentAsync(
                session, Incident.PoiClosureType, itemId, detectedAt: Now.AddMinutes(-10).AddSeconds(index))).Id);
        }

        var spy = new SpyGenerator();

        await Sweeper(kit, spy).SweepAsync(Now, CancellationToken.None);

        spy.IncidentIds.Should().Equal(ids.Take(20));
    }

    [Fact]
    public async Task Sweep_AFailingGeneratorDoesNotStopTheSweepOrThrow()
    {
        await using var kit = await ReroutingTestKit.CreateAsync();
        var (session, stops) = await SessionAsync(kit);
        var itemId = kit.ItemIdOf(session, stops[0]);
        var first = await kit.AddIncidentAsync(session, Incident.PoiClosureType, itemId, detectedAt: Now.AddMinutes(-5));
        var second = await kit.AddIncidentAsync(session, Incident.RouteDeviationType, itemId, detectedAt: Now.AddMinutes(-4));
        var spy = new SpyGenerator { ThrowFor = first.Id };

        var act = () => Sweeper(kit, spy).SweepAsync(Now, CancellationToken.None);

        await act.Should().NotThrowAsync();
        spy.IncidentIds.Should().Equal(first.Id, second.Id);
    }

    private static ReroutingProposalSweeper Sweeper(ReroutingTestKit kit, IReroutingProposalGenerator generator) =>
        new(kit.Db, generator, Options.Create(new ReroutingOptions()));

    private static async Task<(TripSession Session, PointOfInterest[] Stops)> SessionAsync(ReroutingTestKit kit)
    {
        var stops = new[]
        {
            await kit.AddPoiAsync("Stop 1", ReroutingTestKit.NorthByKm(0)),
            await kit.AddPoiAsync("Stop 2", ReroutingTestKit.NorthByKm(0.1)),
            await kit.AddPoiAsync("Stop 3", ReroutingTestKit.NorthByKm(0.2)),
        };
        await kit.AddRequestAsync();
        return (await kit.StartSessionAsync(stops), stops);
    }

    private static async Task<ReroutingEvent> AddEventAsync(
        ReroutingTestKit kit,
        TripSession session,
        PointOfInterest stop,
        DateTimeOffset proposedAt,
        Incident? incident = null)
    {
        incident ??= await kit.AddIncidentAsync(session, Incident.PoiClosureType, kit.ItemIdOf(session, stop));
        var proposal = ReroutingEvent.Propose(incident, session, "{\"schemaVersion\":1}", proposedAt);
        kit.Db.ReroutingEvents.Add(proposal);
        await kit.Db.SaveChangesAsync();
        return proposal;
    }

    private static Task<ReroutingEvent> Reload(ReroutingTestKit kit, ReroutingEvent proposal) =>
        kit.Db.ReroutingEvents.AsNoTracking().SingleAsync(candidate => candidate.Id == proposal.Id);

    internal sealed class SpyGenerator : IReroutingProposalGenerator
    {
        public List<long> IncidentIds { get; } = [];

        public List<DateTimeOffset> Times { get; } = [];

        public long? ThrowFor { get; init; }

        public Action<long>? OnCall { get; init; }

        public Task<bool> TryGenerateAsync(long incidentId, DateTimeOffset nowUtc, CancellationToken cancellationToken)
        {
            IncidentIds.Add(incidentId);
            Times.Add(nowUtc);
            OnCall?.Invoke(incidentId);
            return incidentId == ThrowFor ? throw new InvalidOperationException("boom") : Task.FromResult(false);
        }
    }
}