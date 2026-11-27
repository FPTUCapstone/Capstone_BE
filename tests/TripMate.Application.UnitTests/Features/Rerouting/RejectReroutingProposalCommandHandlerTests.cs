using FluentAssertions;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

using TripMate.Application.Features.Itineraries.Common;
using TripMate.Application.Features.Rerouting.Common;
using TripMate.Application.Features.Rerouting.Reject;
using TripMate.Application.UnitTests.Features.Rerouting.Engine;
using TripMate.Application.UnitTests.TestUtilities;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Application.UnitTests.Features.Rerouting;

/// <summary>UC-15 T7: Keep Current Route. It only closes the proposal; the itinerary and the session never change (PC-02).</summary>
public sealed class RejectReroutingProposalCommandHandlerTests
{
    private static readonly DateTimeOffset ProposedAt = ReroutingTestKit.RequestStart.AddMinutes(30);
    private static readonly DateTimeOffset DecidedAt = ProposedAt.AddMinutes(5);

    [Fact]
    public async Task Reject_ClosesTheProposalAndLeavesTheTripAlone()
    {
        await using var kit = await ReroutingTestKit.CreateAsync();
        var (session, proposalId) = await ProposedAsync(kit);
        var locks = new RecordingLock();
        var itemIds = session.Items.Select(item => item.ItineraryItemId).ToArray();

        var result = await Handler(kit, DecidedAt, locks).Handle(Command(session, proposalId), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        var stored = await kit.Db.ReroutingEvents.AsNoTracking().SingleAsync();
        stored.Status.Should().Be(ReroutingEvent.StatusRejected);
        stored.DecidedAtUtc.Should().Be(DecidedAt);
        locks.Acquired.Should().Equal(kit.Request.Id);
        (await kit.Db.Itineraries.CountAsync()).Should().Be(1);
        var reloaded = await kit.Db.TripSessions.AsNoTracking().Include(candidate => candidate.Items).SingleAsync();
        reloaded.ItineraryId.Should().Be(session.ItineraryId);
        reloaded.Items.Select(item => item.ItineraryItemId).Should().BeEquivalentTo(itemIds);
        (await kit.Db.Incidents.AsNoTracking().SingleAsync()).ResolvedAtUtc.Should().BeNull("the alert itself is not resolved by declining");
    }

    [Fact]
    public async Task Reject_Twice_IsAReplayThatKeepsTheFirstDecisionTime()
    {
        await using var kit = await ReroutingTestKit.CreateAsync();
        var (session, proposalId) = await ProposedAsync(kit);
        await Handler(kit, DecidedAt).Handle(Command(session, proposalId), CancellationToken.None);

        var replay = await Handler(kit, DecidedAt.AddMinutes(1)).Handle(Command(session, proposalId), CancellationToken.None);

        replay.IsSuccess.Should().BeTrue();
        (await kit.Db.ReroutingEvents.AsNoTracking().SingleAsync()).DecidedAtUtc.Should().Be(DecidedAt);
    }

    [Fact]
    public async Task Reject_AfterTheProposalWasAccepted_IsRefused()
    {
        await using var kit = await ReroutingTestKit.CreateAsync();
        var (session, proposalId) = await ProposedAsync(kit);
        (await kit.Db.ReroutingEvents.SingleAsync()).Accept(ProposedAt.AddMinutes(1));
        await kit.Db.SaveChangesAsync();

        var result = await Handler(kit, DecidedAt).Handle(Command(session, proposalId), CancellationToken.None);

        result.ErrorCode.Should().Be(ReroutingErrorCodes.ProposalClosed);
        (await kit.Db.ReroutingEvents.AsNoTracking().SingleAsync()).Status.Should().Be(ReroutingEvent.StatusAccepted);
    }

    [Fact]
    public async Task Reject_OfAnExpiredProposal_IsRefused()
    {
        await using var kit = await ReroutingTestKit.CreateAsync();
        var (session, proposalId) = await ProposedAsync(kit);
        (await kit.Db.ReroutingEvents.SingleAsync()).Expire(ProposedAt.AddMinutes(20));
        await kit.Db.SaveChangesAsync();

        var result = await Handler(kit, DecidedAt).Handle(Command(session, proposalId), CancellationToken.None);

        result.ErrorCode.Should().Be(ReroutingErrorCodes.ProposalExpired);
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(1, false)]
    public async Task Reject_ChecksTheTtlAtTheMomentOfTheCall(int extraMilliseconds, bool allowed)
    {
        await using var kit = await ReroutingTestKit.CreateAsync();
        var (session, proposalId) = await ProposedAsync(kit);
        var now = ProposedAt.AddMinutes(15).AddMilliseconds(extraMilliseconds);

        var result = await Handler(kit, now).Handle(Command(session, proposalId), CancellationToken.None);

        result.IsSuccess.Should().Be(allowed);
        var stored = await kit.Db.ReroutingEvents.AsNoTracking().SingleAsync();
        stored.Status.Should().Be(allowed ? ReroutingEvent.StatusRejected : ReroutingEvent.StatusExpired);
        if (!allowed)
        {
            result.ErrorCode.Should().Be(ReroutingErrorCodes.ProposalExpired);
        }
    }

    [Fact]
    public async Task Reject_WhenTheSessionHasEnded_ExpiresTheProposal()
    {
        await using var kit = await ReroutingTestKit.CreateAsync();
        var (session, proposalId) = await ProposedAsync(kit);
        (await kit.Db.TripSessions.SingleAsync()).Finish(ProposedAt.AddMinutes(1));
        await kit.Db.SaveChangesAsync();

        var result = await Handler(kit, DecidedAt).Handle(Command(session, proposalId), CancellationToken.None);

        result.ErrorCode.Should().Be(ReroutingErrorCodes.ProposalExpired);
        (await kit.Db.ReroutingEvents.AsNoTracking().SingleAsync()).Status.Should().Be(ReroutingEvent.StatusExpired);
    }

    [Fact]
    public async Task Reject_ByAnotherTravelerOrForTheWrongSessionOrProposal_IsNotFound()
    {
        await using var kit = await ReroutingTestKit.CreateAsync();
        var (session, proposalId) = await ProposedAsync(kit);

        foreach (var wrong in new[]
        {
            Command(session, proposalId) with { TravelerUserId = ReroutingTestKit.TravelerId + 1 },
            Command(session, proposalId) with { SessionId = session.Id + 50 },
            Command(session, proposalId) with { ProposalId = proposalId + 50 },
        })
        {
            var result = await Handler(kit, DecidedAt).Handle(wrong, CancellationToken.None);
            result.ErrorCode.Should().Be(ReroutingErrorCodes.NotFound);
        }

        (await kit.Db.ReroutingEvents.AsNoTracking().SingleAsync()).Status.Should().Be(ReroutingEvent.StatusProposed);
    }

    private static RejectReroutingProposalCommandHandler Handler(
        ReroutingTestKit kit,
        DateTimeOffset now,
        RecordingLock? locks = null) =>
        new(
            kit.Db,
            new FakeDateTimeProvider { UtcNow = now },
            Options.Create(new ReroutingOptions()),
            locks ?? new RecordingLock());

    private static RejectReroutingProposalCommand Command(TripSession session, long proposalId) =>
        new(session.Id, proposalId, ReroutingTestKit.TravelerId);

    private static async Task<(TripSession Session, long ProposalId)> ProposedAsync(ReroutingTestKit kit)
    {
        var a = await kit.AddPoiAsync("Stop A", ReroutingTestKit.NorthByKm(0));
        var b = await kit.AddPoiAsync("Stop B", ReroutingTestKit.NorthByKm(0.1), "Culture", IndoorOutdoorType.Outdoor, reviewed: true);
        var c = await kit.AddPoiAsync("Stop C", ReroutingTestKit.NorthByKm(0.2));
        await kit.AddRequestAsync();
        var session = await kit.StartSessionAsync(a, b, c);
        var incident = await kit.AddIncidentAsync(session, Incident.SevereWeatherType, kit.ItemIdOf(session, b));
        (await ReroutingProposalGeneratorTests.Generator(kit).TryGenerateAsync(incident.Id, ProposedAt, CancellationToken.None))
            .Should().BeTrue();
        return (session, (await kit.Db.ReroutingEvents.SingleAsync()).Id);
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