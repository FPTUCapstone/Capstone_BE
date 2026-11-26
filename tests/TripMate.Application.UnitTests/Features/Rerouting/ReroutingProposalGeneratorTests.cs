using FluentAssertions;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

using TripMate.Application.Features.Itineraries.Common;
using TripMate.Application.Features.Rerouting.Common;
using TripMate.Application.Features.Rerouting.Engine;
using TripMate.Application.Features.Rerouting.Generate;
using TripMate.Application.Features.Scheduling.Common;
using TripMate.Application.Features.TripAlerts.Common;
using TripMate.Application.UnitTests.Features.Rerouting.Engine;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Application.UnitTests.Features.Rerouting;

public sealed class ReroutingProposalGeneratorTests
{
    private static readonly DateTimeOffset Now = ReroutingTestKit.RequestStart.AddMinutes(30);

    [Fact]
    public async Task SevereWeather_StoresOneProposedEventWithAValidSnapshot()
    {
        await using var kit = await ReroutingTestKit.CreateAsync();
        var (session, b) = await SevereWeatherScenarioAsync(kit);
        var incident = await kit.AddIncidentAsync(session, Incident.SevereWeatherType, kit.ItemIdOf(session, b));

        var created = await Generator(kit).TryGenerateAsync(incident.Id, Now, CancellationToken.None);

        created.Should().BeTrue();
        var stored = await kit.Db.ReroutingEvents.SingleAsync();
        stored.IncidentId.Should().Be(incident.Id);
        stored.SessionId.Should().Be(session.Id);
        stored.Status.Should().Be(ReroutingEvent.StatusProposed);
        stored.ProposedAtUtc.Should().Be(Now);
        stored.DecidedAtUtc.Should().BeNull();
        ReroutingProposalSnapshot.TryParse(stored.ProposedItinerarySnapshot)!.Strategy.Should().Be(ReroutingStrategies.Drop);
    }

    [Fact]
    public async Task PoiClosure_StoresAProposal()
    {
        await using var kit = await ReroutingTestKit.CreateAsync();
        var (a, b, c) = await ThreeStopsAsync(kit);
        _ = await kit.AddPoiAsync("Another museum", ReroutingTestKit.NorthByKm(0.4), "Culture");
        await kit.AddRequestAsync();
        var session = await kit.StartSessionAsync(a, b, c);
        var incident = await kit.AddIncidentAsync(session, Incident.PoiClosureType, kit.ItemIdOf(session, b));

        (await Generator(kit).TryGenerateAsync(incident.Id, Now, CancellationToken.None)).Should().BeTrue();

        (await kit.Db.ReroutingEvents.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task RouteDeviation_StoresAProposal()
    {
        await using var kit = await ReroutingTestKit.CreateAsync();
        var far = await kit.AddPoiAsync("Far", ReroutingTestKit.NorthByKm(3));
        var near = await kit.AddPoiAsync("Near", ReroutingTestKit.NorthByKm(0.5));
        await kit.AddRequestAsync();
        var session = await kit.StartSessionAsync(far, near);
        var incident = await kit.AddIncidentAsync(session, Incident.RouteDeviationType, kit.ItemIdOf(session, far));

        (await Generator(kit).TryGenerateAsync(incident.Id, Now, CancellationToken.None)).Should().BeTrue();

        (await kit.Db.ReroutingEvents.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task ScheduleDelay_StoresAProposalWhenTheDayNoLongerFits()
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

        (await Generator(kit).TryGenerateAsync(incident.Id, Now, CancellationToken.None)).Should().BeTrue();

        (await kit.Db.ReroutingEvents.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task WeatherAdvisory_NeverGetsAProposal()
    {
        await using var kit = await ReroutingTestKit.CreateAsync();
        var (session, b) = await SevereWeatherScenarioAsync(kit);
        var incident = await kit.AddIncidentAsync(session, Incident.WeatherAdvisoryType, kit.ItemIdOf(session, b));

        (await Generator(kit).TryGenerateAsync(incident.Id, Now, CancellationToken.None)).Should().BeFalse();

        (await kit.Db.ReroutingEvents.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task OverstayDelayOnTheExploringStop_NeverGetsAProposal()
    {
        await using var kit = await ReroutingTestKit.CreateAsync();
        var (session, b) = await SevereWeatherScenarioAsync(kit);
        var itemId = kit.ItemIdOf(session, b);
        var incident = await kit.AddIncidentAsync(
            session, Incident.ScheduleDelayType, itemId, $"{TripAlertResponse.OverstaySourcePrefix}{itemId}");

        (await Generator(kit).TryGenerateAsync(incident.Id, Now, CancellationToken.None)).Should().BeFalse();

        (await kit.Db.ReroutingEvents.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task WhenTheAffectedStopIsNoLongerPending_NoProposalIsStored()
    {
        await using var kit = await ReroutingTestKit.CreateAsync();
        var (session, b) = await SevereWeatherScenarioAsync(kit);
        var incident = await kit.AddIncidentAsync(session, Incident.SevereWeatherType, kit.ItemIdOf(session, b));
        session.ReachItem(kit.ItemIdOf(session, b), Now.AddMinutes(-1));
        await kit.Db.SaveChangesAsync();

        (await Generator(kit).TryGenerateAsync(incident.Id, Now, CancellationToken.None)).Should().BeFalse();

        (await kit.Db.ReroutingEvents.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task WhenTheSessionHasEnded_NoProposalIsStored()
    {
        await using var kit = await ReroutingTestKit.CreateAsync();
        var (session, b) = await SevereWeatherScenarioAsync(kit);
        var incident = await kit.AddIncidentAsync(session, Incident.SevereWeatherType, kit.ItemIdOf(session, b));
        session.Finish(Now.AddMinutes(-1));
        await kit.Db.SaveChangesAsync();

        (await Generator(kit).TryGenerateAsync(incident.Id, Now, CancellationToken.None)).Should().BeFalse();

        (await kit.Db.ReroutingEvents.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task OneProposalPerIncident_EvenAfterItWasClosed()
    {
        await using var kit = await ReroutingTestKit.CreateAsync();
        var (session, b) = await SevereWeatherScenarioAsync(kit);
        var incident = await kit.AddIncidentAsync(session, Incident.SevereWeatherType, kit.ItemIdOf(session, b));
        var generator = Generator(kit);
        await generator.TryGenerateAsync(incident.Id, Now, CancellationToken.None);
        (await kit.Db.ReroutingEvents.SingleAsync()).Reject(Now.AddMinutes(1));
        await kit.Db.SaveChangesAsync();

        (await generator.TryGenerateAsync(incident.Id, Now.AddMinutes(2), CancellationToken.None)).Should().BeFalse();

        (await kit.Db.ReroutingEvents.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task OnlyOneUnexpiredProposalPerSession_ANewOneIsAllowedOnceTheOpenOneExpired()
    {
        await using var kit = await ReroutingTestKit.CreateAsync();
        var (session, b) = await SevereWeatherScenarioAsync(kit);
        var first = await kit.AddIncidentAsync(session, Incident.SevereWeatherType, kit.ItemIdOf(session, b));
        var second = await kit.AddIncidentAsync(session, Incident.PoiClosureType, kit.ItemIdOf(session, b));
        var generator = Generator(kit);
        await generator.TryGenerateAsync(first.Id, Now, CancellationToken.None);

        (await generator.TryGenerateAsync(second.Id, Now.AddMinutes(10), CancellationToken.None))
            .Should().BeFalse("the first proposal is still open at +10 minutes");
        (await generator.TryGenerateAsync(second.Id, Now.AddMinutes(15), CancellationToken.None))
            .Should().BeFalse("the first proposal is still open exactly at the TTL boundary");
        (await generator.TryGenerateAsync(second.Id, Now.AddMinutes(15).AddSeconds(1), CancellationToken.None))
            .Should().BeTrue("the first proposal has passed its TTL, even though nothing marked it Expired yet");

        (await kit.Db.ReroutingEvents.CountAsync()).Should().Be(2);
    }

    [Fact]
    public async Task WhenTheEngineHasNothingToPropose_NothingIsStored()
    {
        await using var kit = await ReroutingTestKit.CreateAsync();
        var (a, b, c) = await ThreeStopsAsync(kit, mandatoryB: true);
        await kit.AddRequestAsync([b.Id]);
        var session = await kit.StartSessionAsync(a, b, c);
        var incident = await kit.AddIncidentAsync(session, Incident.SevereWeatherType, kit.ItemIdOf(session, b));

        (await Generator(kit).TryGenerateAsync(incident.Id, Now, CancellationToken.None)).Should().BeFalse();

        (await kit.Db.ReroutingEvents.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task WhenTheEngineThrows_TheErrorIsSwallowedAndNothingIsStored()
    {
        await using var kit = await ReroutingTestKit.CreateAsync();
        var (session, b) = await SevereWeatherScenarioAsync(kit);
        var incident = await kit.AddIncidentAsync(session, Incident.SevereWeatherType, kit.ItemIdOf(session, b));
        var generator = new ReroutingProposalGenerator(
            kit.Db,
            new ReroutingProposalBuilder(kit.Db, new ThrowingVersionService(), Options.Create(new ReroutingOptions())),
            Options.Create(new ReroutingOptions()));

        var created = await generator.TryGenerateAsync(incident.Id, Now, CancellationToken.None);

        created.Should().BeFalse();
        (await kit.Db.ReroutingEvents.CountAsync()).Should().Be(0);
        (await kit.Db.Incidents.SingleAsync()).ResolvedAtUtc.Should().BeNull("the alert itself is untouched");
    }

    [Fact]
    public async Task Cancellation_IsNotSwallowed()
    {
        await using var kit = await ReroutingTestKit.CreateAsync();
        var (session, b) = await SevereWeatherScenarioAsync(kit);
        var incident = await kit.AddIncidentAsync(session, Incident.SevereWeatherType, kit.ItemIdOf(session, b));
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        var act = () => Generator(kit).TryGenerateAsync(incident.Id, Now, cancelled.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task UnknownIncident_StoresNothing()
    {
        await using var kit = await ReroutingTestKit.CreateAsync();

        (await Generator(kit).TryGenerateAsync(12345, Now, CancellationToken.None)).Should().BeFalse();
    }

    internal static ReroutingProposalGenerator Generator(ReroutingTestKit kit) =>
        new(
            kit.Db,
            new ReroutingProposalBuilder(kit.Db, kit.Service, Options.Create(new ReroutingOptions())),
            Options.Create(new ReroutingOptions()));

    /// <summary>[A, B, C] where B is an Outdoor stop reviewed as such: a severe-weather incident on B yields a Drop.</summary>
    private static async Task<(TripSession Session, PointOfInterest B)> SevereWeatherScenarioAsync(ReroutingTestKit kit)
    {
        var a = await kit.AddPoiAsync("Stop A", ReroutingTestKit.NorthByKm(0));
        var b = await kit.AddPoiAsync("Stop B", ReroutingTestKit.NorthByKm(0.1), "Culture", IndoorOutdoorType.Outdoor, reviewed: true);
        var c = await kit.AddPoiAsync("Stop C", ReroutingTestKit.NorthByKm(0.2));
        await kit.AddRequestAsync();
        return (await kit.StartSessionAsync(a, b, c), b);
    }

    private static async Task<(PointOfInterest A, PointOfInterest B, PointOfInterest C)> ThreeStopsAsync(
        ReroutingTestKit kit,
        bool mandatoryB = false)
    {
        var a = await kit.AddPoiAsync("Stop A", ReroutingTestKit.NorthByKm(0));
        var b = await kit.AddPoiAsync("Stop B", ReroutingTestKit.NorthByKm(0.1), "Culture", IndoorOutdoorType.Outdoor, reviewed: true);
        var c = await kit.AddPoiAsync("Stop C", ReroutingTestKit.NorthByKm(0.2));
        if (!mandatoryB)
        {
            await kit.AddRequestAsync();
        }

        return (a, b, c);
    }

    private sealed class ThrowingVersionService : IItineraryVersionService
    {
        public Task<TripMate.Application.Common.Models.Result<Itinerary>> CreateRegeneratedVersionAsync(
            Itinerary source, SchedulingRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<TripMate.Application.Common.Models.Result<Itinerary>> CreateAdjustedVersionAsync(
            Itinerary source, SchedulingRequest request, IReadOnlyList<long> orderedVisitPoiIds, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<TripMate.Application.Common.Models.Result<Itinerary>> CreateAdjustedVersionAsync(
            Itinerary source, SchedulingRequest request, IReadOnlyList<ItineraryStop> stops, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<TripMate.Application.Common.Models.Result<IReadOnlyList<StopsEvaluation>>> EvaluateStopVariantsAsync(
            SchedulingRequest request,
            IReadOnlyList<IReadOnlyList<ItineraryStop>> variants,
            CancellationToken cancellationToken,
            int? availableMinutesOverride = null,
            EvaluationAnchor? anchor = null) =>
            throw new InvalidOperationException("the routing provider is down");

        public Task<TripMate.Application.Common.Models.Result<Itinerary>> CreateReroutedVersionAsync(
            Itinerary source,
            SchedulingRequest request,
            IReadOnlyList<ItineraryItem> keptItems,
            GeneratedItineraryPlan remainingPlan,
            int firstNewSequenceNo,
            DateTimeOffset nowUtc,
            CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}