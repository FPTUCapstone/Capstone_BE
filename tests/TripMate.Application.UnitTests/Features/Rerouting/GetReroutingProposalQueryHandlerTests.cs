using FluentAssertions;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

using TripMate.Application.Features.Rerouting.Accept;
using TripMate.Application.Features.Rerouting.Common;
using TripMate.Application.Features.Rerouting.Get;
using TripMate.Application.Features.Rerouting.Reject;
using TripMate.Application.UnitTests.Features.Rerouting.Engine;
using TripMate.Application.UnitTests.TestUtilities;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Application.UnitTests.Features.Rerouting;

public sealed class GetReroutingProposalQueryHandlerTests
{
    private static readonly DateTimeOffset Now = ReroutingTestKit.RequestStart.AddMinutes(30);

    [Fact]
    public async Task Handle_ReturnsTheComparisonForTheOwner()
    {
        await using var kit = await ReroutingTestKit.CreateAsync();
        var (session, proposal, b) = await ProposedAsync(kit);

        var result = await Handler(kit, Now.AddMinutes(1)).Handle(
            new GetReroutingProposalQuery(session.Id, proposal.Id, ReroutingTestKit.TravelerId), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        var detail = result.Value;
        detail.ProposalId.Should().Be(proposal.Id);
        detail.IncidentId.Should().Be(proposal.IncidentId);
        detail.Status.Should().Be(ReroutingEvent.StatusProposed);
        detail.Reason.Code.Should().Be("trip_alert.severe_weather");
        detail.Reason.Strategy.Should().Be(ReroutingStrategies.Drop);
        detail.Reason.AffectedItemId.Should().Be(kit.ItemIdOf(session, b));
        detail.Reason.AffectedPoiName.Should().Be("Stop B");
        detail.CurrentStops.Select(stop => stop.Name).Should().Equal("Stop A", "Stop B", "Stop C");
        detail.CurrentStops.Select(stop => stop.ItineraryItemId).Should().Equal(
            session.Items.OrderBy(item => item.SequenceNo).Select(item => item.ItineraryItemId));
        detail.ProposedStops.Select(stop => stop.Name).Should().Equal("Stop A", "Stop C");
        detail.ProposedStops.Should().OnlyContain(stop => stop.Change == ReroutingStopChanges.Kept);
        detail.AffectedStops.Should().ContainSingle().Which.PoiId.Should().Be(b.Id);
        detail.RemovedStops.Should().ContainSingle().Which.Name.Should().Be("Stop B");
        detail.TravelTimeDiffMinutes.Should().Be(-10);
        detail.ArrivalTimeDiffMinutes.Should().BeNegative();
        detail.ProposedAtUtc.Should().Be(Now);
        detail.ExpiresAtUtc.Should().Be(Now.AddMinutes(15));
        detail.DecidedAtUtc.Should().BeNull();
    }

    [Theory]
    [InlineData(14, 59, ReroutingEvent.StatusProposed)]
    [InlineData(15, 0, ReroutingEvent.StatusProposed)]
    [InlineData(15, 1, ReroutingEvent.StatusExpired)]
    public async Task Handle_ReportsAnOverdueProposalAsExpiredWithoutWritingIt(int minutes, int seconds, string expected)
    {
        await using var kit = await ReroutingTestKit.CreateAsync();
        var (session, proposal, _) = await ProposedAsync(kit);

        var result = await Handler(kit, Now.AddMinutes(minutes).AddSeconds(seconds)).Handle(
            new GetReroutingProposalQuery(session.Id, proposal.Id, ReroutingTestKit.TravelerId), CancellationToken.None);

        result.Value.Status.Should().Be(expected);
        (await kit.Db.ReroutingEvents.AsNoTracking().SingleAsync()).Status
            .Should().Be(ReroutingEvent.StatusProposed, "a read never writes");
    }

    [Fact]
    public async Task Handle_ReportsTheStoredDecision()
    {
        await using var kit = await ReroutingTestKit.CreateAsync();
        var (session, proposal, _) = await ProposedAsync(kit);
        proposal.Reject(Now.AddMinutes(2));
        await kit.Db.SaveChangesAsync();

        var result = await Handler(kit, Now.AddHours(1)).Handle(
            new GetReroutingProposalQuery(session.Id, proposal.Id, ReroutingTestKit.TravelerId), CancellationToken.None);

        result.Value.Status.Should().Be(ReroutingEvent.StatusRejected);
        result.Value.DecidedAtUtc.Should().Be(Now.AddMinutes(2));
    }

    [Fact]
    public async Task Handle_ForAnotherTraveler_IsNotFound()
    {
        await using var kit = await ReroutingTestKit.CreateAsync();
        var (session, proposal, _) = await ProposedAsync(kit);

        var result = await Handler(kit, Now).Handle(
            new GetReroutingProposalQuery(session.Id, proposal.Id, ReroutingTestKit.TravelerId + 1), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ReroutingErrorCodes.NotFound);
    }

    [Fact]
    public async Task Handle_ForAProposalOfAnotherSessionOrAnUnknownId_IsNotFound()
    {
        await using var kit = await ReroutingTestKit.CreateAsync();
        var (session, proposal, _) = await ProposedAsync(kit);
        var query = new GetReroutingProposalQuery(session.Id, proposal.Id, ReroutingTestKit.TravelerId);

        foreach (var wrong in new[]
        {
            query with { SessionId = session.Id + 100 },
            query with { ProposalId = proposal.Id + 100 },
        })
        {
            var result = await Handler(kit, Now).Handle(wrong, CancellationToken.None);
            result.ErrorCode.Should().Be(ReroutingErrorCodes.NotFound);
        }
    }

    [Fact]
    public async Task Handle_ForAUatDemoRowWithoutASchemaVersion_IsNotFound()
    {
        await using var kit = await ReroutingTestKit.CreateAsync();
        var (session, proposal, _) = await ProposedAsync(kit);
        var demo = ReroutingEvent.Propose(
            await kit.Db.Incidents.SingleAsync(), session, "{\"demo\":true,\"route\":\"bridge detour\"}", Now);
        kit.Db.ReroutingEvents.Add(demo);
        await kit.Db.SaveChangesAsync();

        var result = await Handler(kit, Now).Handle(
            new GetReroutingProposalQuery(session.Id, demo.Id, ReroutingTestKit.TravelerId), CancellationToken.None);

        result.ErrorCode.Should().Be(ReroutingErrorCodes.NotFound);
        proposal.Id.Should().NotBe(demo.Id);
    }

    [Fact]
    public void Validators_RejectNonPositiveIds()
    {
        new GetReroutingProposalQueryValidator().Validate(new GetReroutingProposalQuery(0, 1, 1)).IsValid.Should().BeFalse();
        new GetReroutingProposalQueryValidator().Validate(new GetReroutingProposalQuery(1, 0, 1)).IsValid.Should().BeFalse();
        new GetReroutingProposalQueryValidator().Validate(new GetReroutingProposalQuery(1, 1, 1)).IsValid.Should().BeTrue();
        new AcceptReroutingProposalCommandValidator().Validate(new AcceptReroutingProposalCommand(0, 1, 1)).IsValid.Should().BeFalse();
        new AcceptReroutingProposalCommandValidator().Validate(new AcceptReroutingProposalCommand(1, -1, 1)).IsValid.Should().BeFalse();
        new AcceptReroutingProposalCommandValidator().Validate(new AcceptReroutingProposalCommand(1, 1, 1)).IsValid.Should().BeTrue();
        new RejectReroutingProposalCommandValidator().Validate(new RejectReroutingProposalCommand(1, 0, 1)).IsValid.Should().BeFalse();
        new RejectReroutingProposalCommandValidator().Validate(new RejectReroutingProposalCommand(1, 1, 1)).IsValid.Should().BeTrue();
    }

    private static GetReroutingProposalQueryHandler Handler(ReroutingTestKit kit, DateTimeOffset now) =>
        new(kit.Db, new FakeDateTimeProvider { UtcNow = now }, Options.Create(new ReroutingOptions()));

    /// <summary>[A, B, C] with B a reviewed Outdoor stop; a severe-weather incident on B gives a stored Drop proposal.</summary>
    private static async Task<(TripSession Session, ReroutingEvent Proposal, PointOfInterest B)> ProposedAsync(ReroutingTestKit kit)
    {
        var a = await kit.AddPoiAsync("Stop A", ReroutingTestKit.NorthByKm(0));
        var b = await kit.AddPoiAsync("Stop B", ReroutingTestKit.NorthByKm(0.1), "Culture", IndoorOutdoorType.Outdoor, reviewed: true);
        var c = await kit.AddPoiAsync("Stop C", ReroutingTestKit.NorthByKm(0.2));
        await kit.AddRequestAsync();
        var session = await kit.StartSessionAsync(a, b, c);
        var incident = await kit.AddIncidentAsync(session, Incident.SevereWeatherType, kit.ItemIdOf(session, b));
        (await ReroutingProposalGeneratorTests.Generator(kit).TryGenerateAsync(incident.Id, Now, CancellationToken.None))
            .Should().BeTrue();
        return (session, await kit.Db.ReroutingEvents.SingleAsync(), b);
    }
}