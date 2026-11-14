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

    private static DerivedBehaviorSignalRecorder Recorder(ApplicationDbContext db) => new(db);

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