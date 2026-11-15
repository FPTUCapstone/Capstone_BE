using FluentAssertions;

using Microsoft.EntityFrameworkCore;

using TripMate.Application.Features.RecommendationFeedback.Derived;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;
using TripMate.Infrastructure.Persistence;

namespace TripMate.Application.UnitTests.Features.RecommendationFeedback;

/// <summary>
/// UC-10 item 6 (spec .scratch/uc10-behavior-learning, D1/D6/D7): behavior signals are derived from what the
/// traveler does, appended to the existing behavior event table, and never written twice for the same action.
/// </summary>
public sealed class DerivedBehaviorSignalRecorderTests
{
    private const long TravelerId = 11;

    private static readonly DateTimeOffset Now = new(2026, 11, 11, 4, 0, 0, TimeSpan.Zero);

    private static readonly Guid Operation = Guid.Parse("7a1c7e6e-2a9f-4f55-9c1a-0d8f1b6e2a10");

    [Fact]
    public async Task ItineraryEdit_RecordsDislikeForRemovedAndLikeForAddedVisits()
    {
        await using var db = CreateDb();
        var (a, b, c, d) = await SeedPois(db);
        var source = await SeedItinerary(db, (a.Id, 1, true), (b.Id, 2, false), (c.Id, 3, false));

        var written = await Recorder(db).RecordItineraryEditAsync(
            TravelerId, source.Id, [a.Id, c.Id, d.Id], Operation, Now, CancellationToken.None);

        written.Should().Be(2);
        var events = await db.RecommendationBehaviorEvents.OrderBy(e => e.PointOfInterestId).ToListAsync();
        events.Should().HaveCount(2);
        var removed = events.Single(e => e.PointOfInterestId == b.Id);
        removed.EventType.Should().Be(RecommendationEventType.Dislike);
        removed.Source.Should().Be(RecommendationCaptureSource.ItineraryEdit);
        removed.ItineraryId.Should().Be(source.Id);
        removed.WasMandatory.Should().BeFalse();
        removed.TravelerUserId.Should().Be(TravelerId);
        removed.OccurredAtUtc.Should().Be(Now);
        removed.OriginalPosition.Should().BeNull();
        var added = events.Single(e => e.PointOfInterestId == d.Id);
        added.EventType.Should().Be(RecommendationEventType.Like);
        added.Source.Should().Be(RecommendationCaptureSource.ItineraryEdit);
        added.ItineraryId.Should().Be(source.Id);
        added.WasMandatory.Should().BeFalse();
    }

    [Fact]
    public async Task ItineraryEdit_RemovedMandatoryVisit_KeepsWasMandatory()
    {
        await using var db = CreateDb();
        var (a, _, _, _) = await SeedPois(db);
        var source = await SeedItinerary(db, (a.Id, 1, true));

        await Recorder(db).RecordItineraryEditAsync(TravelerId, source.Id, [], Operation, Now, CancellationToken.None);

        var removed = await db.RecommendationBehaviorEvents.SingleAsync();
        removed.EventType.Should().Be(RecommendationEventType.Dislike);
        removed.WasMandatory.Should().BeTrue();
    }

    [Fact]
    public async Task ItineraryEdit_ReorderOnly_RecordsNothing()
    {
        await using var db = CreateDb();
        var (a, b, _, _) = await SeedPois(db);
        var source = await SeedItinerary(db, (a.Id, 1, false), (b.Id, 2, false));

        var written = await Recorder(db).RecordItineraryEditAsync(
            TravelerId, source.Id, [b.Id, a.Id], Operation, Now, CancellationToken.None);

        written.Should().Be(0);
        (await db.RecommendationBehaviorEvents.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task ItineraryEdit_SameOperationTwice_RecordsOnce()
    {
        await using var db = CreateDb();
        var (a, b, _, _) = await SeedPois(db);
        var source = await SeedItinerary(db, (a.Id, 1, false), (b.Id, 2, false));
        var recorder = Recorder(db);

        await recorder.RecordItineraryEditAsync(TravelerId, source.Id, [a.Id], Operation, Now, CancellationToken.None);
        db.ChangeTracker.Clear();
        var retry = await recorder.RecordItineraryEditAsync(
            TravelerId, source.Id, [a.Id], Operation, Now.AddSeconds(5), CancellationToken.None);

        retry.Should().Be(0);
        (await db.RecommendationBehaviorEvents.CountAsync()).Should().Be(1);
    }

    // Traveler feedback UI (spec .scratch/uc10-behavior-learning/feedback-ui-spec.md, D10/D15/D16): the traveler
    // says why a stop is removed, and only "not interested" is a dislike.

    [Theory]
    [InlineData(StopRemovalReason.NoTime)]
    [InlineData(StopRemovalReason.AlreadyVisited)]
    public async Task ItineraryEdit_AStopRemovedForAReasonOtherThanTaste_RecordsNothing(StopRemovalReason reason)
    {
        await using var db = CreateDb();
        var (a, b, _, _) = await SeedPois(db);
        var source = await SeedItinerary(db, (a.Id, 1, false), (b.Id, 2, false));

        var written = await Recorder(db).RecordItineraryEditAsync(
            TravelerId, source.Id, [a.Id], Reasons((b.Id, reason)), Operation, Now, CancellationToken.None);

        written.Should().Be(0);
        (await db.RecommendationBehaviorEvents.CountAsync()).Should().Be(0);
    }

    // D19: "already visited" is no dislike, but the place is marked as visited so it can rest for a while.

    [Fact]
    public async Task ItineraryEdit_AStopRemovedBecauseAlreadyVisited_IsMarkedAsVisited()
    {
        await using var db = CreateDb();
        var (a, b, c, _) = await SeedPois(db);
        var source = await SeedItinerary(db, (a.Id, 1, false), (b.Id, 2, false), (c.Id, 3, false));

        await Recorder(db).RecordItineraryEditAsync(
            TravelerId, source.Id, [a.Id],
            Reasons((b.Id, StopRemovalReason.AlreadyVisited), (c.Id, StopRemovalReason.NoTime)), Operation, Now,
            CancellationToken.None);

        var mark = await db.PoiVisitedMarks.SingleAsync();
        mark.TravelerUserId.Should().Be(TravelerId);
        mark.PointOfInterestId.Should().Be(b.Id);
        mark.MarkedAtUtc.Should().Be(Now);
        (await db.RecommendationBehaviorEvents.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task ItineraryEdit_MarkingTheSamePlaceVisitedAgain_KeepsOneMarkWithTheLatestDate()
    {
        await using var db = CreateDb();
        var (a, b, _, _) = await SeedPois(db);
        var first = await SeedItinerary(db, (a.Id, 1, false), (b.Id, 2, false));
        var second = await SeedItinerary(db, (a.Id, 1, false), (b.Id, 2, false));
        var recorder = Recorder(db);

        await recorder.RecordItineraryEditAsync(
            TravelerId, first.Id, [a.Id], Reasons((b.Id, StopRemovalReason.AlreadyVisited)), Operation, Now,
            CancellationToken.None);
        await recorder.RecordItineraryEditAsync(
            TravelerId, second.Id, [a.Id], Reasons((b.Id, StopRemovalReason.AlreadyVisited)), Guid.NewGuid(),
            Now.AddDays(5), CancellationToken.None);

        (await db.PoiVisitedMarks.SingleAsync()).MarkedAtUtc.Should().Be(Now.AddDays(5));
    }

    [Fact]
    public async Task ItineraryEdit_AnAlreadyVisitedReasonForAPlaceThatWasNotRemoved_MarksNothing()
    {
        await using var db = CreateDb();
        var (a, b, _, d) = await SeedPois(db);
        var source = await SeedItinerary(db, (a.Id, 1, false), (b.Id, 2, false));

        // a stays in the itinerary; d was never in it.
        await Recorder(db).RecordItineraryEditAsync(
            TravelerId, source.Id, [a.Id, b.Id],
            Reasons((a.Id, StopRemovalReason.AlreadyVisited), (d.Id, StopRemovalReason.AlreadyVisited)), Operation, Now,
            CancellationToken.None);

        (await db.PoiVisitedMarks.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task ItineraryEdit_AStopRemovedBecauseNotInterested_IsADislike()
    {
        await using var db = CreateDb();
        var (a, b, _, _) = await SeedPois(db);
        var source = await SeedItinerary(db, (a.Id, 1, false), (b.Id, 2, false));

        await Recorder(db).RecordItineraryEditAsync(
            TravelerId, source.Id, [a.Id], Reasons((b.Id, StopRemovalReason.NotInterested)), Operation, Now,
            CancellationToken.None);

        var removed = await db.RecommendationBehaviorEvents.SingleAsync();
        removed.PointOfInterestId.Should().Be(b.Id);
        removed.EventType.Should().Be(RecommendationEventType.Dislike);
        removed.Source.Should().Be(RecommendationCaptureSource.ItineraryEdit);
    }

    [Fact]
    public async Task ItineraryEdit_AReasonForOneStop_SaysNothingAboutAnotherRemovedStop()
    {
        await using var db = CreateDb();
        var (a, b, c, d) = await SeedPois(db);
        var source = await SeedItinerary(db, (a.Id, 1, false), (b.Id, 2, false), (c.Id, 3, false));

        // b has a reason, c has none (an older app): c stays a dislike. d was never in the itinerary.
        await Recorder(db).RecordItineraryEditAsync(
            TravelerId, source.Id, [a.Id],
            Reasons((b.Id, StopRemovalReason.NoTime), (d.Id, StopRemovalReason.NotInterested)), Operation, Now,
            CancellationToken.None);

        var removed = await db.RecommendationBehaviorEvents.SingleAsync();
        removed.PointOfInterestId.Should().Be(c.Id);
        removed.EventType.Should().Be(RecommendationEventType.Dislike);
    }

    private static Dictionary<long, StopRemovalReason> Reasons(params (long PoiId, StopRemovalReason Reason)[] reasons) =>
        reasons.ToDictionary(entry => entry.PoiId, entry => entry.Reason);

    // Found by the local E2E on 2026-11-11: the system places rest stops itself (sometimes at a POI) and recomputes
    // them on every adjust. They are not the traveler's choice, so they must never become behavior signals.

    [Fact]
    public async Task ItineraryEdit_ARestStopTheSystemPlaced_IsNotARemoval()
    {
        await using var db = CreateDb();
        var (a, b, rest, _) = await SeedPois(db);
        var source = await SeedItinerary(db, (a.Id, 1, false), (b.Id, 2, false));
        await AddRestStopAsync(db, source, rest.Id, sequence: 3);

        var written = await Recorder(db).RecordItineraryEditAsync(
            TravelerId, source.Id, [a.Id, b.Id], Operation, Now, CancellationToken.None);

        written.Should().Be(0);
        (await db.RecommendationBehaviorEvents.CountAsync()).Should().Be(0);
    }

    [Theory]
    [InlineData(RecommendationEventType.Like)]
    [InlineData(RecommendationEventType.Skip)]
    public async Task Navigation_ARestStop_RecordsNothing(RecommendationEventType eventType)
    {
        await using var db = CreateDb();
        var (a, rest, _, _) = await SeedPois(db);
        var itinerary = await SeedItinerary(db, (a.Id, 1, false));
        await AddRestStopAsync(db, itinerary, rest.Id, sequence: 2);
        var session = await SeedSession(db, itinerary);
        var restItem = session.Items.Single(candidate => candidate.PoiId == rest.Id);

        var written = await Recorder(db).RecordNavigationAsync(
            session, restItem.ItineraryItemId, eventType, Now, CancellationToken.None);

        written.Should().Be(0);
        (await db.RecommendationBehaviorEvents.CountAsync()).Should().Be(0);
    }

    private static async Task AddRestStopAsync(ApplicationDbContext db, Itinerary itinerary, long poiId, int sequence)
    {
        itinerary.AddItem(ItineraryItem.CreateRest(
            sequence,
            poiId,
            Now.AddHours(sequence),
            Now.AddHours(sequence).AddMinutes(30),
            "Suggested rest stop"));
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task Navigation_ReachedStop_RecordsLikeWithTheItineraryContext()
    {
        await using var db = CreateDb();
        var (a, b, _, _) = await SeedPois(db);
        var itinerary = await SeedItinerary(db, (a.Id, 1, true), (b.Id, 2, false));
        var session = await SeedSession(db, itinerary);
        var item = session.Items.Single(candidate => candidate.PoiId == b.Id);

        await Recorder(db).RecordNavigationAsync(
            session, item.ItineraryItemId, RecommendationEventType.Like, Now, CancellationToken.None);

        var stored = await db.RecommendationBehaviorEvents.SingleAsync();
        stored.EventType.Should().Be(RecommendationEventType.Like);
        stored.Source.Should().Be(RecommendationCaptureSource.Navigation);
        stored.PointOfInterestId.Should().Be(b.Id);
        stored.ItineraryId.Should().Be(itinerary.Id);
        stored.WasMandatory.Should().BeFalse();
        stored.OriginalPosition.Should().BeNull();
        stored.TravelerUserId.Should().Be(TravelerId);
    }

    [Fact]
    public async Task Navigation_SkippedStop_RecordsSkipAtTheStopPosition()
    {
        await using var db = CreateDb();
        var (a, _, _, _) = await SeedPois(db);
        var itinerary = await SeedItinerary(db, (a.Id, 1, true));
        var session = await SeedSession(db, itinerary);
        var item = session.Items.Single();

        await Recorder(db).RecordNavigationAsync(
            session, item.ItineraryItemId, RecommendationEventType.Skip, Now, CancellationToken.None);

        var stored = await db.RecommendationBehaviorEvents.SingleAsync();
        stored.EventType.Should().Be(RecommendationEventType.Skip);
        stored.Source.Should().Be(RecommendationCaptureSource.Navigation);
        stored.OriginalPosition.Should().Be(1);
        stored.WasMandatory.Should().BeTrue();
    }

    [Fact]
    public async Task Navigation_UnknownItem_RecordsNothing()
    {
        await using var db = CreateDb();
        var (a, _, _, _) = await SeedPois(db);
        var session = await SeedSession(db, await SeedItinerary(db, (a.Id, 1, false)));

        var written = await Recorder(db).RecordNavigationAsync(
            session, itineraryItemId: 999, RecommendationEventType.Like, Now, CancellationToken.None);

        written.Should().Be(0);
    }

    [Fact]
    public async Task Mandatory_RecordsOneLikePerMandatoryPoi_Once()
    {
        await using var db = CreateDb();
        var (a, b, _, _) = await SeedPois(db);
        var recorder = Recorder(db);

        var first = await recorder.RecordMandatoryAsync(TravelerId, 501, [a.Id, b.Id], Now, CancellationToken.None);
        db.ChangeTracker.Clear();
        var retry = await recorder.RecordMandatoryAsync(TravelerId, 501, [a.Id, b.Id], Now, CancellationToken.None);

        first.Should().Be(2);
        retry.Should().Be(0);
        var events = await db.RecommendationBehaviorEvents.ToListAsync();
        events.Should().HaveCount(2);
        events.Should().OnlyContain(e =>
            e.EventType == RecommendationEventType.Like
            && e.Source == RecommendationCaptureSource.SchedulingRequest
            && e.ItineraryId == null
            && e.WasMandatory == true
            && e.TravelerUserId == TravelerId);
    }

    [Fact]
    public void ClientEventId_IsDeterministicPerSourceOperationPoiAndType()
    {
        var first = DerivedBehaviorSignalRecorder.ClientEventIdFor(
            RecommendationCaptureSource.ItineraryEdit, "op-1", 20, RecommendationEventType.Dislike);
        var same = DerivedBehaviorSignalRecorder.ClientEventIdFor(
            RecommendationCaptureSource.ItineraryEdit, "op-1", 20, RecommendationEventType.Dislike);
        var otherType = DerivedBehaviorSignalRecorder.ClientEventIdFor(
            RecommendationCaptureSource.ItineraryEdit, "op-1", 20, RecommendationEventType.Like);
        var otherSource = DerivedBehaviorSignalRecorder.ClientEventIdFor(
            RecommendationCaptureSource.Navigation, "op-1", 20, RecommendationEventType.Dislike);

        same.Should().Be(first);
        otherType.Should().NotBe(first);
        otherSource.Should().NotBe(first);
    }

    [Fact]
    public async Task SaveFailure_IsSwallowedAndLeavesNothingTracked()
    {
        await using var db = new FailingSaveDbContext(Options());
        var (a, _, _, _) = await SeedPois(db);
        db.FailNextSave = true;

        var written = await Recorder(db).RecordMandatoryAsync(TravelerId, 501, [a.Id], Now, CancellationToken.None);

        written.Should().Be(0);
        db.ChangeTracker.Entries<RecommendationBehaviorEvent>().Should().BeEmpty();
        (await db.RecommendationBehaviorEvents.CountAsync()).Should().Be(0);
    }

    // Taking an edit back (specs/UC-10-undo-behavior-signal-spec.md, U1-U6): Undo re-sends the previous stops, so a
    // stop the traveler put back, or an added stop they took back, is no new opinion about the place.

    [Fact]
    public async Task PutBack_AfterARemovalForLackOfTime_RecordsNothing()
    {
        await using var db = CreateDb();
        var (a, b, _, _) = await SeedPois(db);
        var (source, _) = await SeedAdjustedAsync(
            db, previous: [a.Id, b.Id], current: [a.Id], Reasons((b.Id, StopRemovalReason.NoTime)));

        var written = await Recorder(db).RecordItineraryEditAsync(
            TravelerId, source.Id, [a.Id, b.Id], Operation, Now.AddMinutes(1), CancellationToken.None);

        written.Should().Be(0);
        (await db.RecommendationBehaviorEvents.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task PutBack_AfterAnAlreadyVisitedRemoval_RecordsNothingAndRemovesThatVisitedMark()
    {
        await using var db = CreateDb();
        var (a, b, _, _) = await SeedPois(db);
        // The mark is dated exactly when the removal was saved: "at or after" must include the same instant.
        var (source, _) = await SeedAdjustedAsync(
            db, previous: [a.Id, b.Id], current: [a.Id], Reasons((b.Id, StopRemovalReason.AlreadyVisited)));
        (await db.PoiVisitedMarks.SingleAsync()).MarkedAtUtc.Should().Be(Now);

        var written = await Recorder(db).RecordItineraryEditAsync(
            TravelerId, source.Id, [a.Id, b.Id], Operation, Now.AddMinutes(1), CancellationToken.None);

        written.Should().Be(0);
        (await db.RecommendationBehaviorEvents.CountAsync()).Should().Be(0);
        (await db.PoiVisitedMarks.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task PutBack_KeepsAVisitedMarkFromBeforeTheRemoval()
    {
        await using var db = CreateDb();
        var (a, b, _, _) = await SeedPois(db);
        db.PoiVisitedMarks.Add(PoiVisitedMark.Create(TravelerId, b.Id, Now.AddTicks(-1)));
        await db.SaveChangesAsync();
        var (source, _) = await SeedAdjustedAsync(
            db, previous: [a.Id, b.Id], current: [a.Id], Reasons((b.Id, StopRemovalReason.NoTime)));

        await Recorder(db).RecordItineraryEditAsync(
            TravelerId, source.Id, [a.Id, b.Id], Operation, Now.AddMinutes(1), CancellationToken.None);

        (await db.PoiVisitedMarks.SingleAsync()).MarkedAtUtc.Should().Be(Now.AddTicks(-1));
    }

    [Theory]
    [InlineData(StopRemovalReason.NotInterested)]
    [InlineData(null)]
    public async Task PutBack_AfterARemovalRecordedAsADislike_IsALikeThatCancelsIt(StopRemovalReason? reason)
    {
        await using var db = CreateDb();
        var (a, b, _, _) = await SeedPois(db);
        var (source, previous) = await SeedAdjustedAsync(
            db, previous: [a.Id, b.Id], current: [a.Id], reason is { } given ? Reasons((b.Id, given)) : null);

        var written = await Recorder(db).RecordItineraryEditAsync(
            TravelerId, source.Id, [a.Id, b.Id], Operation, Now.AddMinutes(1), CancellationToken.None);

        written.Should().Be(1);
        var events = await db.RecommendationBehaviorEvents.OrderBy(e => e.OccurredAtUtc).ToListAsync();
        events.Select(e => (e.PointOfInterestId, e.EventType, e.ItineraryId)).Should().Equal(
            (b.Id, RecommendationEventType.Dislike, (long?)previous.Id),
            (b.Id, RecommendationEventType.Like, (long?)source.Id));
    }

    [Fact]
    public async Task PutBack_ADislikeFromAnOlderEdit_IsNotTheReasonOfTheLastRemoval()
    {
        await using var db = CreateDb();
        var (a, b, _, _) = await SeedPois(db);
        // Long ago, in another itinerary, B was removed as a dislike.
        var old = await SeedItinerary(db, (a.Id, 1, false), (b.Id, 2, false));
        await Recorder(db).RecordItineraryEditAsync(
            TravelerId, old.Id, [a.Id], Reasons((b.Id, StopRemovalReason.NotInterested)), Guid.NewGuid(),
            Now.AddDays(-40), CancellationToken.None);
        var (source, _) = await SeedAdjustedAsync(
            db, previous: [a.Id, b.Id], current: [a.Id], Reasons((b.Id, StopRemovalReason.NoTime)));

        var written = await Recorder(db).RecordItineraryEditAsync(
            TravelerId, source.Id, [a.Id, b.Id], Operation, Now.AddMinutes(1), CancellationToken.None);

        written.Should().Be(0);
        var only = await db.RecommendationBehaviorEvents.SingleAsync();
        only.EventType.Should().Be(RecommendationEventType.Dislike);
        only.ItineraryId.Should().Be(old.Id);
    }

    [Fact]
    public async Task TakenBackAdd_WithoutAReason_IsNoDislike()
    {
        await using var db = CreateDb();
        var (a, _, _, d) = await SeedPois(db);
        var (source, previous) = await SeedAdjustedAsync(db, previous: [a.Id], current: [a.Id, d.Id], reasons: null);

        var written = await Recorder(db).RecordItineraryEditAsync(
            TravelerId, source.Id, [a.Id], Operation, Now.AddMinutes(1), CancellationToken.None);

        written.Should().Be(0);
        var only = await db.RecommendationBehaviorEvents.SingleAsync();
        only.EventType.Should().Be(RecommendationEventType.Like);
        only.ItineraryId.Should().Be(previous.Id);
    }

    [Fact]
    public async Task TakenBackAdd_WithANotInterestedReason_IsADislike()
    {
        await using var db = CreateDb();
        var (a, _, _, d) = await SeedPois(db);
        var (source, _) = await SeedAdjustedAsync(db, previous: [a.Id], current: [a.Id, d.Id], reasons: null);

        var written = await Recorder(db).RecordItineraryEditAsync(
            TravelerId, source.Id, [a.Id], Reasons((d.Id, StopRemovalReason.NotInterested)), Operation,
            Now.AddMinutes(1), CancellationToken.None);

        written.Should().Be(1);
        (await db.RecommendationBehaviorEvents.CountAsync(e =>
            e.PointOfInterestId == d.Id && e.EventType == RecommendationEventType.Dislike)).Should().Be(1);
    }

    [Fact]
    public async Task AfterAnAdjust_OtherAddsAndRemovalsAreUnchanged()
    {
        await using var db = CreateDb();
        var (a, b, c, d) = await SeedPois(db);
        // The edit before only added D: B was in both versions and C in neither.
        var (source, _) = await SeedAdjustedAsync(
            db, previous: [a.Id, b.Id], current: [a.Id, b.Id, d.Id], reasons: null);

        var written = await Recorder(db).RecordItineraryEditAsync(
            TravelerId, source.Id, [a.Id, c.Id, d.Id], Operation, Now.AddMinutes(1), CancellationToken.None);

        written.Should().Be(2);
        var events = await db.RecommendationBehaviorEvents.Where(e => e.ItineraryId == source.Id).ToListAsync();
        events.Select(e => (e.PointOfInterestId, e.EventType)).Should().BeEquivalentTo(new[]
        {
            (b.Id, RecommendationEventType.Dislike),
            (c.Id, RecommendationEventType.Like),
        });
    }

    [Fact]
    public async Task AfterARegenerate_ThereIsNoPreviousVersionToTakeBack()
    {
        await using var db = CreateDb();
        var (a, b, _, d) = await SeedPois(db);
        var previous = await SeedItinerary(db, (a.Id, 1, false), (b.Id, 2, false));
        var source = await SeedItinerary(db, (a.Id, 1, false), (d.Id, 2, false));
        await LinkAsync(db, previous, source, ItineraryVersionOperationType.Regenerate, Guid.NewGuid());

        var written = await Recorder(db).RecordItineraryEditAsync(
            TravelerId, source.Id, [a.Id, b.Id], Operation, Now.AddMinutes(1), CancellationToken.None);

        written.Should().Be(2);
        var events = await db.RecommendationBehaviorEvents.ToListAsync();
        events.Select(e => (e.PointOfInterestId, e.EventType)).Should().BeEquivalentTo(new[]
        {
            (b.Id, RecommendationEventType.Like),
            (d.Id, RecommendationEventType.Dislike),
        });
    }

    [Fact]
    public async Task WhenThePreviousVersionCannotBeRead_TheEditRecordsWhatItRecordedBefore()
    {
        await using var db = new UnreadableOperationsDbContext(Options());
        var (a, b, _, _) = await SeedPois(db);
        var (source, _) = await SeedAdjustedAsync(
            db, previous: [a.Id, b.Id], current: [a.Id], Reasons((b.Id, StopRemovalReason.NoTime)));
        db.FailOperationReads = true;

        var written = await new DerivedBehaviorSignalRecorder(db).RecordItineraryEditAsync(
            TravelerId, source.Id, [a.Id, b.Id], Operation, Now.AddMinutes(1), CancellationToken.None);

        written.Should().Be(1);
        (await db.RecommendationBehaviorEvents.SingleAsync()).EventType.Should().Be(RecommendationEventType.Like);
    }

    [Fact]
    public async Task ItineraryEdit_SaveFailure_IsLoggedWhenTheCallerPassesALogger()
    {
        await using var db = new FailingSaveDbContext(Options());
        var (a, b, _, _) = await SeedPois(db);
        var source = await SeedItinerary(db, (a.Id, 1, false), (b.Id, 2, false));
        var logger = new RecordingLogger();
        db.FailNextSave = true;

        var written = await new DerivedBehaviorSignalRecorder(db, logger).RecordItineraryEditAsync(
            TravelerId, source.Id, [a.Id], Operation, Now, CancellationToken.None);

        written.Should().Be(0);
        logger.Messages.Should().ContainSingle(message => message.StartsWith("derived_behavior_signal_failed"));
    }

    private sealed class RecordingLogger : Microsoft.Extensions.Logging.ILogger
    {
        public List<string> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;

        public void Log<TState>(
            Microsoft.Extensions.Logging.LogLevel logLevel,
            Microsoft.Extensions.Logging.EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            Messages.Add(formatter(state, exception));
    }

    private static DerivedBehaviorSignalRecorder Recorder(ApplicationDbContext db) => new(db);

    /// <summary>
    /// Two versions linked by an adjust saved at <see cref="Now"/>, with the signals that adjust recorded: what the
    /// database holds once the traveler has edited <paramref name="previous"/> into <paramref name="current"/>.
    /// </summary>
    private static async Task<(Itinerary Source, Itinerary Previous)> SeedAdjustedAsync(
        ApplicationDbContext db,
        long[] previous,
        long[] current,
        Dictionary<long, StopRemovalReason>? reasons)
    {
        var before = await SeedItinerary(db, previous.Select((id, index) => (id, index + 1, false)).ToArray());
        var after = await SeedItinerary(db, current.Select((id, index) => (id, index + 1, false)).ToArray());
        var key = Guid.NewGuid();
        await LinkAsync(db, before, after, ItineraryVersionOperationType.Adjust, key);
        await new DerivedBehaviorSignalRecorder(db).RecordItineraryEditAsync(
            TravelerId, before.Id, current, reasons, key, Now, CancellationToken.None);
        return (after, before);
    }

    private static async Task LinkAsync(
        ApplicationDbContext db,
        Itinerary previous,
        Itinerary result,
        ItineraryVersionOperationType type,
        Guid key)
    {
        var operation = ItineraryVersionOperation.Create(TravelerId, previous.Id, type, key, "hash", Now);
        db.ItineraryVersionOperations.Add(operation);
        await db.SaveChangesAsync();
        operation.Complete(result.Id);
        await db.SaveChangesAsync();
    }

    private static DbContextOptions<ApplicationDbContext> Options() =>
        new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

    private static ApplicationDbContext CreateDb() => new(Options());

    private static async Task<(PointOfInterest A, PointOfInterest B, PointOfInterest C, PointOfInterest D)> SeedPois(
        ApplicationDbContext db)
    {
        var category = PoiCategory.Create($"Category-{Guid.NewGuid():N}", null);
        var pois = new[] { "A", "B", "C", "D" }
            .Select(name => PointOfInterest.Create(category, name, 16m, 108m, 1, Now))
            .ToArray();
        db.PointsOfInterest.AddRange(pois);
        await db.SaveChangesAsync();
        return (pois[0], pois[1], pois[2], pois[3]);
    }

    private static async Task<Itinerary> SeedItinerary(
        ApplicationDbContext db,
        params (long PoiId, int Sequence, bool Mandatory)[] items)
    {
        var itinerary = Itinerary.CreateManual(TravelerId, "Test", Itinerary.ActiveStatus, Now);
        foreach (var item in items)
        {
            itinerary.AddItem(ItineraryItem.CreateVisit(
                item.Sequence,
                item.PoiId,
                Now.AddHours(item.Sequence),
                Now.AddHours(item.Sequence).AddMinutes(30),
                item.Mandatory,
                null,
                null));
        }

        db.Itineraries.Add(itinerary);
        await db.SaveChangesAsync();
        return itinerary;
    }

    private static async Task<TripSession> SeedSession(ApplicationDbContext db, Itinerary itinerary)
    {
        var pois = await db.PointsOfInterest.ToDictionaryAsync(poi => poi.Id);
        var session = TripSession.Start(
            itinerary.Id,
            itinerary.Id,
            TravelerId,
            Guid.NewGuid(),
            Now,
            Now.AddHours(12),
            itinerary.Items.OrderBy(item => item.SequenceNo).Select(item => TripSessionItem.Snapshot(
                item.Id,
                item.SequenceNo,
                item.PointOfInterestId!.Value,
                pois[item.PointOfInterestId!.Value].Name,
                16m,
                108m,
                item.PlannedArrivalUtc!.Value,
                item.PlannedDepartureUtc!.Value,
                item.IsMandatory)));
        db.TripSessions.Add(session);
        await db.SaveChangesAsync();
        return session;
    }

    /// <summary>The version history is unreadable through the application port (for example a storage error).</summary>
    private sealed class UnreadableOperationsDbContext(DbContextOptions<ApplicationDbContext> options)
        : ApplicationDbContext(options), TripMate.Application.Common.Interfaces.IApplicationDbContext
    {
        public bool FailOperationReads { get; set; }

        DbSet<ItineraryVersionOperation> TripMate.Application.Common.Interfaces.IApplicationDbContext.ItineraryVersionOperations =>
            FailOperationReads
                ? throw new NotSupportedException("The version history is not available.")
                : Set<ItineraryVersionOperation>();
    }

    private sealed class FailingSaveDbContext(DbContextOptions<ApplicationDbContext> options)
        : ApplicationDbContext(options)
    {
        public bool FailNextSave { get; set; }

        public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
        {
            if (FailNextSave)
            {
                FailNextSave = false;
                throw new DbUpdateException("CHECK constraint failed (migration not applied).");
            }

            return base.SaveChangesAsync(cancellationToken);
        }
    }
}