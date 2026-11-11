using FluentAssertions;

using Microsoft.EntityFrameworkCore;

using TripMate.Application.Features.Scheduling.Personalization;
using TripMate.Application.UnitTests.TestUtilities;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;
using TripMate.Infrastructure.Persistence;

namespace TripMate.Application.UnitTests.Features.Scheduling.Personalization;

/// <summary>
/// UC-10 item 6 (spec .scratch/uc10-behavior-learning §3.3, D2/D3): the profile sent to the AI is the traveler's
/// liked and disliked POIs, weighted by signal kind, halved every 90 days, netted per POI and capped per side.
/// </summary>
public sealed class BehaviorProfileBuilderTests
{
    private const long TravelerId = 11;
    private const long ItineraryId = 30;

    private static readonly DateTimeOffset Now = new(2026, 11, 11, 4, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task NoEvents_ReturnsNoProfile()
    {
        await using var db = CreateDb();
        await SeedPois(db, 1);

        (await Build(db)).Should().BeNull();
    }

    [Fact]
    public async Task RecentSignalsWeighMore_HalvingEveryNinetyDays()
    {
        await using var db = CreateDb();
        var pois = await SeedPois(db, 3);
        await AddEvents(
            db,
            Like(pois[0], daysAgo: 0),
            Like(pois[1], daysAgo: 90),
            Like(pois[2], daysAgo: 180));

        PoiRankingBehavior profile = (await Build(db))!;

        profile.Disliked.Should().BeEmpty();
        profile.Liked.Select(poi => poi.PoiId).Should().Equal(pois[0].Id, pois[1].Id, pois[2].Id);
        profile.Liked.Select(poi => poi.Weight).Should().Equal(1.0m, 0.5m, 0.25m);
    }

    [Fact]
    public async Task EachSignalKind_HasItsOwnWeightAndSide()
    {
        await using var db = CreateDb();
        var pois = await SeedPois(db, 8);
        await AddEvents(
            db,
            Like(pois[0]),
            Event(pois[1], RecommendationEventType.Like, RecommendationCaptureSource.ItineraryEdit),
            Event(pois[2], RecommendationEventType.Like, RecommendationCaptureSource.SchedulingRequest),
            Event(pois[3], RecommendationEventType.Like, RecommendationCaptureSource.Navigation),
            Event(pois[4], RecommendationEventType.Dislike, RecommendationCaptureSource.Explore),
            Event(pois[5], RecommendationEventType.Dislike, RecommendationCaptureSource.ItineraryEdit),
            Event(pois[6], RecommendationEventType.Skip, RecommendationCaptureSource.Navigation),
            Event(pois[7], RecommendationEventType.Skip, RecommendationCaptureSource.Itinerary));

        PoiRankingBehavior profile = (await Build(db))!;

        Weights(profile.Liked).Should().Equal(new Dictionary<long, decimal>
        {
            [pois[0].Id] = 1.0m,
            [pois[1].Id] = 1.0m,
            [pois[2].Id] = 0.5m,
            [pois[3].Id] = 0.3m,
        });
        Weights(profile.Disliked).Should().Equal(new Dictionary<long, decimal>
        {
            [pois[4].Id] = 1.0m,
            [pois[5].Id] = 1.0m,
            [pois[6].Id] = 0.7m,
            [pois[7].Id] = 0.5m,
        });
    }

    [Fact]
    public async Task OppositeSignalsOnOnePoi_NetOutInsteadOfCountingTwice()
    {
        await using var db = CreateDb();
        var pois = await SeedPois(db, 2);
        await AddEvents(
            db,
            // Liked, then removed from an itinerary: no evidence either way.
            Like(pois[0]),
            Event(pois[0], RecommendationEventType.Dislike, RecommendationCaptureSource.ItineraryEdit),
            // Liked, then skipped once during navigation: still liked, but less.
            Like(pois[1]),
            Event(pois[1], RecommendationEventType.Skip, RecommendationCaptureSource.Navigation));

        PoiRankingBehavior profile = (await Build(db))!;

        Weights(profile.Liked).Should().Equal(new Dictionary<long, decimal> { [pois[1].Id] = 0.3m });
        profile.Disliked.Should().BeEmpty();
    }

    [Fact]
    public async Task KeepsOnlyTheHeaviestPoisPerSide()
    {
        await using var db = CreateDb();
        var pois = await SeedPois(db, 6);
        await AddEvents(db, pois.Select((poi, index) => Like(poi, daysAgo: index * 30)).ToArray());

        PoiRankingBehavior profile = (await Build(db, new PersonalizationRankingOptions { MaxBehaviorPoisPerSide = 4 }))!;

        profile.Liked.Select(poi => poi.PoiId).Should().Equal(pois.Take(4).Select(poi => poi.Id));
    }

    [Fact]
    public async Task IgnoresOldEvents_Reorders_AndOtherTravelers()
    {
        await using var db = CreateDb();
        var pois = await SeedPois(db, 3);
        await AddEvents(
            db,
            Like(pois[0], daysAgo: 366),
            RecommendationBehaviorEvent.Create(
                TravelerId, pois[1].Id, ItineraryId, RecommendationEventType.Reorder, 1, 2, false,
                RecommendationCaptureSource.Itinerary, Now, Guid.NewGuid()),
            RecommendationBehaviorEvent.Create(
                TravelerId + 1, pois[2].Id, null, RecommendationEventType.Like, null, null, null,
                RecommendationCaptureSource.Explore, Now, Guid.NewGuid()));

        (await Build(db)).Should().BeNull();
    }

    [Fact]
    public async Task ResolvesNameCategoryAndTags_SoTheAiCanEmbedPoisOutsideTheCandidatePool()
    {
        await using var db = CreateDb();
        var category = PoiCategory.Create("Museum", null);
        var poi = PointOfInterest.Create(category, "Cham Museum", 16m, 108m, 1, Now);
        poi.AddTag(Tag.Create("history"));
        poi.AddTag(Tag.Create("culture"));
        db.PointsOfInterest.Add(poi);
        await db.SaveChangesAsync();
        await AddEvents(db, Like(poi));

        PoiRankingBehaviorPoi liked = (await Build(db))!.Liked.Single();

        liked.PoiId.Should().Be(poi.Id);
        liked.Name.Should().Be("Cham Museum");
        liked.CategoryName.Should().Be("Museum");
        liked.TagNames.Should().BeEquivalentTo("history", "culture");
        liked.Weight.Should().Be(1.0m);
    }

    // Decision D8 (2026-11-11): a stop the traveler removed or skipped is not suggested again for 14 days.

    [Fact]
    public async Task RemovedRecently_ListsStopsRemovedOrSkippedInTheLastFourteenDays()
    {
        await using var db = CreateDb();
        var pois = await SeedPois(db, 5);
        await AddEvents(
            db,
            Event(pois[0], RecommendationEventType.Dislike, RecommendationCaptureSource.ItineraryEdit, daysAgo: 3),
            Event(pois[1], RecommendationEventType.Skip, RecommendationCaptureSource.Navigation, daysAgo: 14),
            Event(pois[2], RecommendationEventType.Dislike, RecommendationCaptureSource.ItineraryEdit, daysAgo: 15),
            Like(pois[3], daysAgo: 1),
            RecommendationBehaviorEvent.Create(
                TravelerId + 1, pois[4].Id, ItineraryId, RecommendationEventType.Dislike, null, null, false,
                RecommendationCaptureSource.ItineraryEdit, Now, Guid.NewGuid()));

        var removed = await Builder(db).RemovedRecentlyAsync(TravelerId, CancellationToken.None);

        removed.Should().BeEquivalentTo([pois[0].Id, pois[1].Id]);
    }

    [Fact]
    public async Task RemovedRecently_AStopTheTravelerChoseAgainAfterRemovingIt_IsNoLongerListed()
    {
        await using var db = CreateDb();
        var pois = await SeedPois(db, 3);
        await AddEvents(
            db,
            // Removed, then added back while adjusting.
            Event(pois[0], RecommendationEventType.Dislike, RecommendationCaptureSource.ItineraryEdit, daysAgo: 5),
            Event(pois[0], RecommendationEventType.Like, RecommendationCaptureSource.ItineraryEdit, daysAgo: 2),
            // Removed, then marked mandatory in a new request.
            Event(pois[1], RecommendationEventType.Dislike, RecommendationCaptureSource.ItineraryEdit, daysAgo: 5),
            Event(pois[1], RecommendationEventType.Like, RecommendationCaptureSource.SchedulingRequest, daysAgo: 1),
            // Liked first, removed afterwards: still cooling down.
            Like(pois[2], daysAgo: 5),
            Event(pois[2], RecommendationEventType.Dislike, RecommendationCaptureSource.ItineraryEdit, daysAgo: 2));

        var removed = await Builder(db).RemovedRecentlyAsync(TravelerId, CancellationToken.None);

        removed.Should().BeEquivalentTo([pois[2].Id]);
    }

    [Fact]
    public async Task RemovedRecently_WithTheCooldownTurnedOff_ListsNothing()
    {
        await using var db = CreateDb();
        var pois = await SeedPois(db, 1);
        await AddEvents(db, Event(pois[0], RecommendationEventType.Dislike, RecommendationCaptureSource.ItineraryEdit));

        var removed = await Builder(db, new PersonalizationRankingOptions { RemovedStopCooldownDays = 0 })
            .RemovedRecentlyAsync(TravelerId, CancellationToken.None);

        removed.Should().BeEmpty();
    }

    private static BehaviorProfileBuilder Builder(
        ApplicationDbContext db,
        PersonalizationRankingOptions? options = null) =>
        new(db, new FakeDateTimeProvider { UtcNow = Now }, options ?? new());

    private static Task<PoiRankingBehavior?> Build(
        ApplicationDbContext db,
        PersonalizationRankingOptions? options = null) =>
        new BehaviorProfileBuilder(db, new FakeDateTimeProvider { UtcNow = Now }, options ?? new())
            .BuildAsync(TravelerId, CancellationToken.None);

    private static Dictionary<long, decimal> Weights(IEnumerable<PoiRankingBehaviorPoi> pois) =>
        pois.ToDictionary(poi => poi.PoiId, poi => poi.Weight);

    private static RecommendationBehaviorEvent Like(PointOfInterest poi, int daysAgo = 0) =>
        Event(poi, RecommendationEventType.Like, RecommendationCaptureSource.Explore, daysAgo);

    private static RecommendationBehaviorEvent Event(
        PointOfInterest poi,
        RecommendationEventType type,
        RecommendationCaptureSource source,
        int daysAgo = 0)
    {
        bool hasItinerary = source is RecommendationCaptureSource.Itinerary
            or RecommendationCaptureSource.ItineraryEdit
            or RecommendationCaptureSource.Navigation;
        return RecommendationBehaviorEvent.Create(
            TravelerId,
            poi.Id,
            hasItinerary ? ItineraryId : null,
            type,
            type == RecommendationEventType.Skip ? 1 : null,
            newPosition: null,
            hasItinerary ? false : source == RecommendationCaptureSource.SchedulingRequest ? true : null,
            source,
            Now.AddDays(-daysAgo),
            Guid.NewGuid());
    }

    private static async Task AddEvents(ApplicationDbContext db, params RecommendationBehaviorEvent[] events)
    {
        db.RecommendationBehaviorEvents.AddRange(events);
        await db.SaveChangesAsync();
    }

    private static async Task<PointOfInterest[]> SeedPois(ApplicationDbContext db, int count)
    {
        var category = PoiCategory.Create($"Category-{Guid.NewGuid():N}", null);
        var pois = Enumerable.Range(1, count)
            .Select(index => PointOfInterest.Create(category, $"POI {index}", 16m, 108m, 1, Now))
            .ToArray();
        db.PointsOfInterest.AddRange(pois);
        await db.SaveChangesAsync();
        return pois;
    }

    private static ApplicationDbContext CreateDb() =>
        new(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);
}