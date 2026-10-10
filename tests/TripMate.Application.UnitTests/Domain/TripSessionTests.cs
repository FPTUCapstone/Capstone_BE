using FluentAssertions;

using TripMate.Domain.Entities;

namespace TripMate.Application.UnitTests.Domain;

public sealed class TripSessionTests
{
    private static readonly DateTimeOffset StartedAtUtc =
        new(2026, 10, 20, 1, 30, 0, TimeSpan.Zero);

    private static readonly DateTimeOffset ExpiresAtUtc = StartedAtUtc.AddHours(12);

    [Fact]
    public void Start_CreatesNavigatingSessionWithFrozenItemsExpiryAndHistory()
    {
        var session = CreateSession();

        session.ItineraryId.Should().Be(91);
        session.RequestedItineraryId.Should().Be(80);
        session.TravelerUserId.Should().Be(7);
        session.FsmState.Should().Be(TripSession.NavigatingState);
        session.StartedAtUtc.Should().Be(StartedAtUtc);
        session.ExpiresAtUtc.Should().Be(ExpiresAtUtc);
        session.EndedAtUtc.Should().BeNull();
        session.ExploringItemId.Should().BeNull();
        session.Items.Select(item => item.ItineraryItemId).Should().Equal(1001, 1002, 1003);
        session.Items.Should().OnlyContain(item => item.Status == TripSessionItem.PendingStatus);
        session.StateHistory.Should().ContainSingle().Which.Should().BeEquivalentTo(new
        {
            FromState = (string?)null,
            ToState = TripSession.NavigatingState,
            Reason = TripStateHistory.NavigationStartedReason,
            TriggeredBy = TripStateHistory.TriggeredByTraveler,
            ChangedAtUtc = StartedAtUtc,
        });
    }

    [Fact]
    public void Start_RejectsEmptySnapshotNonUtcTimesAndExpiryNotAfterStart()
    {
        Action emptySnapshot = () => Start(StartedAtUtc, ExpiresAtUtc, []);
        Action nonUtcStart = () => Start(StartedAtUtc.ToOffset(TimeSpan.FromHours(7)), ExpiresAtUtc, Items());
        Action nonUtcExpiry = () => Start(StartedAtUtc, ExpiresAtUtc.ToOffset(TimeSpan.FromHours(7)), Items());
        Action expiryAtStart = () => Start(StartedAtUtc, StartedAtUtc, Items());

        emptySnapshot.Should().Throw<ArgumentException>();
        nonUtcStart.Should().Throw<ArgumentException>();
        nonUtcExpiry.Should().Throw<ArgumentException>();
        expiryAtStart.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void ReachItem_AllowsAnyOrderAndEntersExploringWithOneHistoryRow()
    {
        var session = CreateSession();
        var reachedAt = StartedAtUtc.AddMinutes(30);

        session.ReachItem(1003, reachedAt).Should().Be(TripSessionProgressOutcome.Applied);

        session.FsmState.Should().Be(TripSession.ExploringState);
        session.ExploringItemId.Should().Be(1003);
        Item(session, 1003).Status.Should().Be(TripSessionItem.ReachedStatus);
        Item(session, 1003).ReachedAtUtc.Should().Be(reachedAt);
        Item(session, 1001).Status.Should().Be(TripSessionItem.PendingStatus);
        session.StateHistory.Should().HaveCount(2);
        session.StateHistory.Last().Should().BeEquivalentTo(new
        {
            FromState = TripSession.NavigatingState,
            ToState = TripSession.ExploringState,
            Reason = TripStateHistory.ItemReachedReason,
            TriggeredBy = TripStateHistory.TriggeredByTraveler,
            ChangedAtUtc = reachedAt,
        });
    }

    [Fact]
    public void ReachItem_WhileExploringSwitchesExploringItemWithoutHistory()
    {
        var session = CreateSession();
        session.ReachItem(1001, StartedAtUtc.AddMinutes(30));

        session.ReachItem(1002, StartedAtUtc.AddMinutes(40)).Should().Be(TripSessionProgressOutcome.Applied);

        session.FsmState.Should().Be(TripSession.ExploringState);
        session.ExploringItemId.Should().Be(1002);
        session.StateHistory.Should().HaveCount(2);
    }

    [Fact]
    public void ReachItem_ReplayKeepsFirstTimestampAndState()
    {
        var session = CreateSession();
        var firstReachedAt = StartedAtUtc.AddMinutes(30);
        session.ReachItem(1001, firstReachedAt);
        session.Depart(StartedAtUtc.AddMinutes(60));

        session.ReachItem(1001, StartedAtUtc.AddMinutes(70)).Should().Be(TripSessionProgressOutcome.Replayed);

        Item(session, 1001).ReachedAtUtc.Should().Be(firstReachedAt);
        session.FsmState.Should().Be(TripSession.NavigatingState);
        session.ExploringItemId.Should().BeNull();
        session.StateHistory.Should().HaveCount(3);
    }

    [Fact]
    public void ReachItem_RejectsUnknownItemAndNewProgressOnCompletedSession()
    {
        var session = CreateSession();
        session.ReachItem(1001, StartedAtUtc.AddMinutes(30));
        session.ReachItem(9999, StartedAtUtc.AddMinutes(31)).Should().Be(TripSessionProgressOutcome.NotInSnapshot);
        session.Finish(StartedAtUtc.AddMinutes(40));

        session.ReachItem(1002, StartedAtUtc.AddMinutes(41)).Should().Be(TripSessionProgressOutcome.SessionCompleted);
        session.ReachItem(1001, StartedAtUtc.AddMinutes(42)).Should().Be(TripSessionProgressOutcome.Replayed);
        Item(session, 1002).ReachedAtUtc.Should().BeNull();
    }

    [Fact]
    public void SkipItem_SkipsAnyPendingItemWithoutStateChangeAndAllowsLaterReach()
    {
        var session = CreateSession();
        var skippedAt = StartedAtUtc.AddMinutes(10);

        session.SkipItem(1002, skippedAt).Should().Be(TripSessionProgressOutcome.Applied);
        session.SkipItem(1002, skippedAt.AddMinutes(5)).Should().Be(TripSessionProgressOutcome.Replayed);

        Item(session, 1002).Status.Should().Be(TripSessionItem.SkippedStatus);
        Item(session, 1002).SkippedAtUtc.Should().Be(skippedAt);
        session.FsmState.Should().Be(TripSession.NavigatingState);
        session.StateHistory.Should().ContainSingle();

        var reachedAt = StartedAtUtc.AddMinutes(90);
        session.ReachItem(1002, reachedAt).Should().Be(TripSessionProgressOutcome.Applied);
        Item(session, 1002).Status.Should().Be(TripSessionItem.ReachedStatus);
        Item(session, 1002).ReachedAtUtc.Should().Be(reachedAt);
        Item(session, 1002).SkippedAtUtc.Should().Be(skippedAt);
    }

    [Fact]
    public void SkipItem_RejectsReachedUnknownAndCompletedSessionItems()
    {
        var session = CreateSession();
        session.ReachItem(1001, StartedAtUtc.AddMinutes(30));

        session.SkipItem(1001, StartedAtUtc.AddMinutes(31)).Should().Be(TripSessionProgressOutcome.AlreadyReached);
        session.SkipItem(9999, StartedAtUtc.AddMinutes(31)).Should().Be(TripSessionProgressOutcome.NotInSnapshot);
        session.Finish(StartedAtUtc.AddMinutes(40));
        session.SkipItem(1002, StartedAtUtc.AddMinutes(41)).Should().Be(TripSessionProgressOutcome.SessionCompleted);

        Item(session, 1001).SkippedAtUtc.Should().BeNull();
        Item(session, 1002).SkippedAtUtc.Should().BeNull();
    }

    [Fact]
    public void Depart_ReturnsToNavigatingOnceAndRejectsCompletedSession()
    {
        var session = CreateSession();
        session.ReachItem(1001, StartedAtUtc.AddMinutes(30));
        var departedAt = StartedAtUtc.AddMinutes(90);

        session.Depart(departedAt).Should().Be(TripSessionProgressOutcome.Applied);
        session.Depart(departedAt.AddMinutes(1)).Should().Be(TripSessionProgressOutcome.Replayed);

        session.FsmState.Should().Be(TripSession.NavigatingState);
        session.ExploringItemId.Should().BeNull();
        session.StateHistory.Should().HaveCount(3);
        session.StateHistory.Last().Should().BeEquivalentTo(new
        {
            FromState = TripSession.ExploringState,
            ToState = TripSession.NavigatingState,
            Reason = TripStateHistory.TravelerDepartedReason,
            TriggeredBy = TripStateHistory.TriggeredByTraveler,
            ChangedAtUtc = departedAt,
        });

        session.Finish(departedAt.AddMinutes(5));
        session.Depart(departedAt.AddMinutes(6)).Should().Be(TripSessionProgressOutcome.SessionCompleted);
    }

    [Fact]
    public void Finish_WithPendingItemsStopsAndPreservesProgress()
    {
        var session = CreateSession();
        session.ReachItem(1001, StartedAtUtc.AddMinutes(30));
        var finishedAt = StartedAtUtc.AddMinutes(45);

        session.Finish(finishedAt).Should().BeTrue();
        session.Finish(finishedAt.AddMinutes(1)).Should().BeFalse();

        session.FsmState.Should().Be(TripSession.CompletedState);
        session.CompletionReason.Should().Be(TripSession.TravelerStoppedReason);
        session.EndedAtUtc.Should().Be(finishedAt);
        session.ExploringItemId.Should().BeNull();
        Item(session, 1001).ReachedAtUtc.Should().NotBeNull();
        session.StateHistory.Should().HaveCount(3);
        session.StateHistory.Last().Should().BeEquivalentTo(new
        {
            FromState = TripSession.ExploringState,
            ToState = TripSession.CompletedState,
            Reason = TripSession.TravelerStoppedReason,
            TriggeredBy = TripStateHistory.TriggeredByTraveler,
            ChangedAtUtc = finishedAt,
        });
    }

    [Fact]
    public void Finish_WithoutPendingItemsRecordsRouteFinishedEvenWhenSomeWereSkipped()
    {
        var session = CreateSession();
        session.ReachItem(1001, StartedAtUtc.AddMinutes(30));
        session.SkipItem(1002, StartedAtUtc.AddMinutes(31));
        session.ReachItem(1003, StartedAtUtc.AddMinutes(60));

        session.Finish(StartedAtUtc.AddMinutes(90)).Should().BeTrue();

        session.CompletionReason.Should().Be(TripSession.RouteFinishedReason);
    }

    [Fact]
    public void ExpireIfDue_ClosesAtExpiryWithSystemTriggerAndCorrectReason()
    {
        var withPending = CreateSession();
        withPending.ReachItem(1001, StartedAtUtc.AddMinutes(30));

        withPending.ExpireIfDue(ExpiresAtUtc.AddTicks(-1)).Should().BeFalse();
        withPending.ExpireIfDue(ExpiresAtUtc.AddHours(3)).Should().BeTrue();
        withPending.ExpireIfDue(ExpiresAtUtc.AddHours(4)).Should().BeFalse();

        withPending.FsmState.Should().Be(TripSession.CompletedState);
        withPending.CompletionReason.Should().Be(TripSession.ExpiredReason);
        withPending.EndedAtUtc.Should().Be(ExpiresAtUtc);
        withPending.ExploringItemId.Should().BeNull();
        withPending.StateHistory.Last().Should().BeEquivalentTo(new
        {
            FromState = TripSession.ExploringState,
            ToState = TripSession.CompletedState,
            Reason = TripSession.ExpiredReason,
            TriggeredBy = TripStateHistory.TriggeredBySystem,
            ChangedAtUtc = ExpiresAtUtc,
        });

        var allResolved = CreateSession();
        allResolved.SkipItem(1001, StartedAtUtc.AddMinutes(1));
        allResolved.SkipItem(1002, StartedAtUtc.AddMinutes(2));
        allResolved.ReachItem(1003, StartedAtUtc.AddMinutes(3));
        allResolved.ExpireIfDue(ExpiresAtUtc).Should().BeTrue();
        allResolved.CompletionReason.Should().Be(TripSession.RouteFinishedReason);
    }

    [Fact]
    public void ExpireIfDue_DoesNotRewriteAnEarlierCompletion()
    {
        var session = CreateSession();
        var finishedAt = StartedAtUtc.AddMinutes(30);
        session.Finish(finishedAt);

        session.ExpireIfDue(ExpiresAtUtc.AddHours(1)).Should().BeFalse();

        session.CompletionReason.Should().Be(TripSession.TravelerStoppedReason);
        session.EndedAtUtc.Should().Be(finishedAt);
    }

    [Fact]
    public void ResolveEventTime_AcceptsOnlyDeviceTimesInsideTheSessionRange()
    {
        var session = CreateSession();
        var now = StartedAtUtc.AddHours(2);
        var tolerance = TimeSpan.FromMinutes(2);

        session.ResolveEventTime(null, now, tolerance).Should().Be(now);
        session.ResolveEventTime(StartedAtUtc.AddMinutes(10), now, tolerance)
            .Should().Be(StartedAtUtc.AddMinutes(10));
        session.ResolveEventTime(StartedAtUtc, now, tolerance).Should().Be(StartedAtUtc);
        session.ResolveEventTime(StartedAtUtc.AddTicks(-1), now, tolerance).Should().Be(now);
        session.ResolveEventTime(now.AddMinutes(1), now, tolerance).Should().Be(now);
        session.ResolveEventTime(now.AddMinutes(3), now, tolerance).Should().Be(now);
        session.ResolveEventTime(StartedAtUtc.AddMinutes(10).ToOffset(TimeSpan.FromHours(7)), now, tolerance)
            .Should().BeExactly(StartedAtUtc.AddMinutes(10));
    }

    [Fact]
    public void ResolveEventTime_NeverPlacesAnEventBeforeTheLastRecordedOne()
    {
        // A device clock running behind the server must not reorder the history: here the
        // reach was recorded with the server clock, then a late device time arrives.
        var session = CreateSession();
        var reachedAt = StartedAtUtc.AddMinutes(30);
        session.ReachItem(1001, reachedAt).Should().Be(TripSessionProgressOutcome.Applied);
        var now = StartedAtUtc.AddHours(2);
        var tolerance = TimeSpan.FromMinutes(2);

        session.ResolveEventTime(StartedAtUtc.AddMinutes(20), now, tolerance).Should().Be(now);
        session.ResolveEventTime(reachedAt, now, tolerance).Should().Be(reachedAt);
        session.ResolveEventTime(reachedAt.AddMinutes(5), now, tolerance)
            .Should().Be(reachedAt.AddMinutes(5));
    }

    [Fact]
    public void Constants_MatchTheContractVocabulary()
    {
        TripSession.NavigatingState.Should().Be("Navigating");
        TripSession.ExploringState.Should().Be("Exploring");
        TripSession.CompletedState.Should().Be("Completed");
        TripSession.RouteFinishedReason.Should().Be("RouteFinished");
        TripSession.TravelerStoppedReason.Should().Be("TravelerStopped");
        TripSession.ExpiredReason.Should().Be("Expired");
        TripStateHistory.ItemReachedReason.Should().Be("ItemReached");
        TripStateHistory.TravelerDepartedReason.Should().Be("TravelerDeparted");
    }

    private static TripSessionItem Item(TripSession session, long itemId) =>
        session.Items.Single(item => item.ItineraryItemId == itemId);

    private static TripSession CreateSession() => Start(StartedAtUtc, ExpiresAtUtc, Items());

    private static TripSession Start(
        DateTimeOffset startedAtUtc,
        DateTimeOffset expiresAtUtc,
        IEnumerable<TripSessionItem> items) =>
        TripSession.Start(
            itineraryId: 91,
            requestedItineraryId: 80,
            travelerUserId: 7,
            startIdempotencyKey: Guid.Parse("11111111-1111-1111-1111-111111111111"),
            startedAtUtc: startedAtUtc,
            expiresAtUtc: expiresAtUtc,
            items: items);

    private static TripSessionItem[] Items() =>
    [
        Snapshot(1003, 3, 25, "My Khe Beach", 16.059700m, 108.247000m),
        Snapshot(1001, 1, 12, "Marble Mountains", 16.003300m, 108.263500m),
        Snapshot(1002, 2, 18, "Dragon Bridge", 16.061100m, 108.227600m),
    ];

    private static TripSessionItem Snapshot(
        long itemId,
        int sequenceNo,
        long poiId,
        string name,
        decimal latitude,
        decimal longitude) =>
        TripSessionItem.Snapshot(
            itemId,
            sequenceNo,
            poiId,
            name,
            latitude,
            longitude,
            StartedAtUtc.AddHours(sequenceNo),
            StartedAtUtc.AddHours(sequenceNo).AddMinutes(90),
            isMandatory: sequenceNo == 1);
}