using FluentAssertions;

using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

using TripMate.Api.IntegrationTests.Infrastructure;
using TripMate.Application.Common.Interfaces;
using TripMate.Application.Features.RecommendationFeedback.Favorites;
using TripMate.Application.Features.Scheduling.Personalization;

namespace TripMate.Api.IntegrationTests.RecommendationFeedback;

/// <summary>
/// UC-10 traveler feedback (D12/D17/D19/D20): the two tables created by 20261112_uc10_traveler_feedback.sql, the
/// EF mapping on them, and the profile queries that read them must work on SQL Server, including before the
/// migration is applied.
/// </summary>
[Collection(nameof(TripMateApiFactory))]
public sealed class PoiFavoritesSqlServerTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 11, 4, 0, 0, TimeSpan.Zero);

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task TheHeart_TurnsOnAndOff_AndCountsAsALikeWhileItIsOn()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        var (travelerId, poiId) = await SeedTravelerAndPoiAsync(database);
        // Removed three days ago, so the place is cooling down until the heart is tapped.
        await database.ExecuteNonQueryAsync($"""
            INSERT INTO social.RecommendationBehaviorEvents (
                traveler_user_id, poi_id, itinerary_id, event_type,
                original_position, new_position, was_mandatory, source, occurred_at_utc, client_event_id)
            VALUES ({travelerId}, {poiId}, NULL, 'Dislike', NULL, NULL, NULL, 'PoiDetail', '2026-11-08T04:00:00', NEWID());
            """);
        await using var db = database.CreateDbContext();
        var clock = new FixedClock(Now);
        var builder = new BehaviorProfileBuilder(db, clock, new PersonalizationRankingOptions());
        (await builder.RemovedRecentlyAsync(travelerId, CancellationToken.None)).Should().Equal(poiId);

        var on = await new SetPoiFavoriteCommandHandler(db, clock)
            .Handle(new SetPoiFavoriteCommand(travelerId, poiId, true), CancellationToken.None);
        var onAgain = await new SetPoiFavoriteCommandHandler(db, clock)
            .Handle(new SetPoiFavoriteCommand(travelerId, poiId, true), CancellationToken.None);

        on.Value.IsFavorite.Should().BeTrue();
        onAgain.Value.IsFavorite.Should().BeTrue();
        (await database.ExecuteScalarAsync<int>(
            $"SELECT COUNT(*) FROM social.PoiFavorites WHERE traveler_user_id = {travelerId} AND poi_id = {poiId};"))
            .Should().Be(1);
        (await new GetPoiFavoriteQueryHandler(db)
            .Handle(new GetPoiFavoriteQuery(travelerId, poiId), CancellationToken.None))
            .Value.IsFavorite.Should().BeTrue();
        // The heart (+1.0, no decay) outweighs the three-day-old dislike, and it lifts the cooldown.
        PoiRankingBehavior profile = (await builder.BuildAsync(travelerId, CancellationToken.None))!;
        profile.Liked.Should().ContainSingle().Which.PoiId.Should().Be(poiId);
        profile.Disliked.Should().BeEmpty();
        (await builder.RemovedRecentlyAsync(travelerId, CancellationToken.None)).Should().BeEmpty();

        var off = await new SetPoiFavoriteCommandHandler(db, clock)
            .Handle(new SetPoiFavoriteCommand(travelerId, poiId, false), CancellationToken.None);

        off.Value.IsFavorite.Should().BeFalse();
        (await database.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM social.PoiFavorites;")).Should().Be(0);
        (await builder.RemovedRecentlyAsync(travelerId, CancellationToken.None)).Should().Equal(poiId);
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task TheTable_KeepsOneRowPerTravelerAndPlace()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        var (travelerId, poiId) = await SeedTravelerAndPoiAsync(database);
        string insert = $"INSERT INTO social.PoiFavorites (traveler_user_id, poi_id) VALUES ({travelerId}, {poiId});";
        await database.ExecuteNonQueryAsync(insert);

        Func<Task> second = () => database.ExecuteNonQueryAsync(insert);

        (await second.Should().ThrowAsync<SqlException>()).Which.Message.Should().Contain("UQ_PoiFavorites_Traveler_POI");
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task AVisitedMark_RestsUntilTheTravelerChoosesThePlaceAgain()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        var (travelerId, markedPoiId) = await SeedTravelerAndPoiAsync(database);
        var (_, likedPoiId) = await SeedTravelerAndPoiAsync(database);
        await using var db = database.CreateDbContext();
        var clock = new FixedClock(Now);
        var builder = new BehaviorProfileBuilder(db, clock, new PersonalizationRankingOptions());

        // "Already visited" three days ago, said again today: one row, the latest date.
        var mark = Domain.Entities.PoiVisitedMark.Create(travelerId, markedPoiId, Now.AddDays(-3));
        db.PoiVisitedMarks.Add(mark);
        await db.SaveChangesAsync();
        mark.MarkAgain(Now);
        await db.SaveChangesAsync();
        // An ordinary like on another place, to show the mark itself adds nothing to the profile.
        await database.ExecuteNonQueryAsync($"""
            INSERT INTO social.RecommendationBehaviorEvents (
                traveler_user_id, poi_id, itinerary_id, event_type,
                original_position, new_position, was_mandatory, source, occurred_at_utc, client_event_id)
            VALUES ({travelerId}, {likedPoiId}, NULL, 'Like', NULL, NULL, NULL, 'Explore', '2026-11-01T04:00:00', NEWID());
            """);

        (await database.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM social.PoiVisitedMarks;")).Should().Be(1);
        (await builder.VisitedRecentlyAsync(travelerId, CancellationToken.None)).Should().Equal(markedPoiId);
        // A mark is neither a like nor a dislike, and it is not a removal.
        (await builder.RemovedRecentlyAsync(travelerId, CancellationToken.None)).Should().BeEmpty();
        PoiRankingBehavior profile = (await builder.BuildAsync(travelerId, CancellationToken.None))!;
        profile.Liked.Should().ContainSingle().Which.PoiId.Should().Be(likedPoiId);
        profile.Disliked.Should().BeEmpty();

        // The heart ends the rest.
        await new SetPoiFavoriteCommandHandler(db, clock)
            .Handle(new SetPoiFavoriteCommand(travelerId, markedPoiId, true), CancellationToken.None);
        (await builder.VisitedRecentlyAsync(travelerId, CancellationToken.None)).Should().BeEmpty();

        string duplicate = $"INSERT INTO social.PoiVisitedMarks (traveler_user_id, poi_id) VALUES ({travelerId}, {markedPoiId});";
        Func<Task> second = () => database.ExecuteNonQueryAsync(duplicate);
        (await second.Should().ThrowAsync<SqlException>()).Which.Message.Should().Contain("UQ_PoiVisitedMarks_Traveler_POI");
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task BeforeTheMigrationIsApplied_TheProfileStillBuildsWithoutFavorites()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        var (travelerId, poiId) = await SeedTravelerAndPoiAsync(database);
        await database.ExecuteNonQueryAsync($"""
            DROP TABLE social.PoiFavorites;
            DROP TABLE social.PoiVisitedMarks;
            INSERT INTO social.RecommendationBehaviorEvents (
                traveler_user_id, poi_id, itinerary_id, event_type,
                original_position, new_position, was_mandatory, source, occurred_at_utc, client_event_id)
            VALUES ({travelerId}, {poiId}, NULL, 'Dislike', NULL, NULL, NULL, 'PoiDetail', '2026-11-08T04:00:00', NEWID());
            """);
        await using var db = database.CreateDbContext();
        var builder = new BehaviorProfileBuilder(db, new FixedClock(Now), new PersonalizationRankingOptions());

        // Regeneration ranks inside a transaction: the failed read must leave it usable.
        await using var transaction = await db.Database.BeginTransactionAsync();
        PoiRankingBehavior profile = (await builder.BuildAsync(travelerId, CancellationToken.None))!;
        var removed = await builder.RemovedRecentlyAsync(travelerId, CancellationToken.None);
        var visited = await builder.VisitedRecentlyAsync(travelerId, CancellationToken.None);
        int pois = await db.PointsOfInterest.CountAsync(poi => poi.Id == poiId);
        await transaction.CommitAsync();

        profile.Disliked.Should().ContainSingle().Which.PoiId.Should().Be(poiId);
        removed.Should().Equal(poiId);
        visited.Should().BeEmpty();
        pois.Should().Be(1);
    }

    private static async Task<(long TravelerId, long PoiId)> SeedTravelerAndPoiAsync(SqlServerTestDatabase database)
    {
        var suffix = Guid.NewGuid().ToString("N");
        var travelerId = await database.ExecuteScalarAsync<long>($"""
            INSERT INTO dbo.Users (role, email, full_name, status)
            OUTPUT INSERTED.user_id
            VALUES ('Traveler', 'favorite-{suffix}@example.test', N'Favorite', 'Active');
            """);
        var categoryId = await database.ExecuteScalarAsync<int>($"""
            INSERT INTO catalog.POICategories (name)
            OUTPUT INSERTED.category_id
            VALUES (N'Museum-{suffix}');
            """);
        var poiId = await database.ExecuteScalarAsync<long>($"""
            INSERT INTO catalog.POIs (category_id, name, latitude, longitude, status, created_by)
            OUTPUT INSERTED.poi_id
            VALUES ({categoryId}, N'Museum-{suffix}', 16.000000, 108.000000, 'Active', {travelerId});
            """);
        return (travelerId, poiId);
    }

    private sealed class FixedClock(DateTimeOffset now) : IDateTimeProvider
    {
        public DateTimeOffset UtcNow { get; } = now;
    }
}