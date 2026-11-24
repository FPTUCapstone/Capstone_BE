using FluentAssertions;

using TripMate.Domain.Entities;

namespace TripMate.Application.UnitTests.Domain;

public sealed class TripSessionRerouteTests
{
    private static readonly DateTimeOffset StartedAt = new(2026, 11, 24, 1, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Now = StartedAt.AddHours(3);

    [Fact]
    public void RemovePendingItems_RemovesOnlyPendingRowsAndReturnsThem()
    {
        var session = SessionWithReachedAndSkipped();

        var removed = session.RemovePendingItems();

        removed.Select(item => item.ItineraryItemId).Should().BeEquivalentTo([1003L, 1004L]);
        session.Items.Select(item => item.ItineraryItemId).Should().BeEquivalentTo([1001L, 1002L]);
        session.Items.Single(item => item.ItineraryItemId == 1001).Status.Should().Be(TripSessionItem.ReachedStatus);
        session.Items.Single(item => item.ItineraryItemId == 1002).Status.Should().Be(TripSessionItem.SkippedStatus);
    }

    [Fact]
    public void RemovePendingItems_OnTerminalSession_ChangesNothing()
    {
        var session = SessionWithReachedAndSkipped();
        session.Finish(Now);

        session.RemovePendingItems().Should().BeEmpty();

        session.Items.Should().HaveCount(4);
    }

    [Fact]
    public void NextItemSequenceNo_ContinuesAfterTheHighestKeptSequence()
    {
        var session = SessionWithReachedAndSkipped();
        session.NextItemSequenceNo.Should().Be(5);

        session.RemovePendingItems();

        session.NextItemSequenceNo.Should().Be(3);
    }

    [Fact]
    public void AttachRerouteItems_RebindsItineraryAddsRowsAndWritesHistoryWithoutChangingState()
    {
        var session = SessionWithReachedAndSkipped();
        var stateBefore = session.FsmState;
        var exploringBefore = session.ExploringItemId;
        var historyBefore = session.StateHistory.Count;
        session.RemovePendingItems();
        var added = new[] { NewItem(2001, 3, 31), NewItem(2002, 4, 32) };

        session.AttachRerouteItems(92, added, Now);

        session.ItineraryId.Should().Be(92);
        session.RequestedItineraryId.Should().Be(80, "the requested itinerary id never changes");
        session.FsmState.Should().Be(stateBefore);
        session.ExploringItemId.Should().Be(exploringBefore);
        session.Items.OrderBy(item => item.SequenceNo).Select(item => item.ItineraryItemId)
            .Should().Equal(1001L, 1002L, 2001L, 2002L);
        added.Should().OnlyContain(item => item.Session == session);
        session.StateHistory.Should().HaveCount(historyBefore + 1);
        var history = session.StateHistory.Last();
        history.Reason.Should().Be(TripStateHistory.RerouteAcceptedReason);
        history.TriggeredBy.Should().Be(TripStateHistory.TriggeredByTraveler);
        history.FromState.Should().Be(stateBefore);
        history.ToState.Should().Be(stateBefore);
        history.ChangedAtUtc.Should().Be(Now);
    }

    [Fact]
    public void RerouteAcceptedReason_IsTheSpecValue() =>
        TripStateHistory.RerouteAcceptedReason.Should().Be("rerouting_accepted");

    [Fact]
    public void AttachRerouteItems_WhilePendingRowsRemain_Throws()
    {
        var session = SessionWithReachedAndSkipped();

        FluentActions.Invoking(() => session.AttachRerouteItems(92, [NewItem(2001, 5, 31)], Now))
            .Should().Throw<InvalidOperationException>();

        session.ItineraryId.Should().Be(91);
    }

    [Fact]
    public void AttachRerouteItems_OnTerminalSession_Throws()
    {
        var session = SessionWithReachedAndSkipped();
        session.RemovePendingItems();
        session.Finish(Now);

        FluentActions.Invoking(() => session.AttachRerouteItems(92, [NewItem(2001, 3, 31)], Now))
            .Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void AttachRerouteItems_RejectsInvalidInput()
    {
        var session = SessionWithReachedAndSkipped();
        session.RemovePendingItems();

        FluentActions.Invoking(() => session.AttachRerouteItems(0, [NewItem(2001, 3, 31)], Now))
            .Should().Throw<ArgumentOutOfRangeException>();
        FluentActions.Invoking(() => session.AttachRerouteItems(92, [], Now))
            .Should().Throw<ArgumentException>();
        FluentActions.Invoking(() => session.AttachRerouteItems(92, [NewItem(2001, 4, 31)], Now))
            .Should().Throw<ArgumentException>("sequence must continue exactly after the kept rows");
        FluentActions.Invoking(() => session.AttachRerouteItems(92, [NewItem(2001, 3, 31), NewItem(2002, 5, 32)], Now))
            .Should().Throw<ArgumentException>("sequence must be consecutive");
        FluentActions.Invoking(() => session.AttachRerouteItems(92, [NewItem(1001, 3, 31)], Now))
            .Should().Throw<ArgumentException>("an itinerary item id may appear only once in the session");
        FluentActions.Invoking(() => session.AttachRerouteItems(92, [NewItem(2001, 3, 31)], Now.ToOffset(TimeSpan.FromHours(7))))
            .Should().Throw<ArgumentException>();

        session.ItineraryId.Should().Be(91);
        session.Items.Should().HaveCount(2);
    }

    [Fact]
    public void AttachRerouteItems_ItemsAreNavigableAfterwards()
    {
        var session = SessionWithReachedAndSkipped();
        session.RemovePendingItems();
        session.AttachRerouteItems(92, [NewItem(2001, 3, 31)], Now);

        session.ReachItem(2001, Now.AddMinutes(10)).Should().Be(TripSessionProgressOutcome.Applied);
        session.ReachItem(1003, Now.AddMinutes(11)).Should().Be(TripSessionProgressOutcome.NotInSnapshot,
            "a replaced stop is no longer part of the snapshot");
    }

    private static TripSession SessionWithReachedAndSkipped()
    {
        var session = TripSession.Start(
            91, 80, 7, Guid.Parse("22222222-2222-2222-2222-222222222222"), StartedAt, StartedAt.AddHours(12),
            [
                NewItem(1001, 1, 11),
                NewItem(1002, 2, 12),
                NewItem(1003, 3, 13),
                NewItem(1004, 4, 14),
            ]);
        session.ReachItem(1001, StartedAt.AddHours(1));
        session.SkipItem(1002, StartedAt.AddHours(2));
        return session;
    }

    private static TripSessionItem NewItem(long itemId, int sequenceNo, long poiId) =>
        TripSessionItem.Snapshot(
            itemId, sequenceNo, poiId, $"POI {poiId}", 16.0m + sequenceNo / 100m, 108.2m,
            StartedAt.AddHours(sequenceNo), StartedAt.AddHours(sequenceNo).AddMinutes(60), isMandatory: false);
}