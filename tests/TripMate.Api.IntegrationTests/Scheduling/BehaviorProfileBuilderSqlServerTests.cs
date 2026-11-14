using FluentAssertions;

using TripMate.Api.IntegrationTests.Infrastructure;
using TripMate.Application.Common.Interfaces;
using TripMate.Application.Features.Scheduling.Personalization;

namespace TripMate.Api.IntegrationTests.Scheduling;

/// <summary>
/// UC-10 item 6: the profile query (time window on a DATETIME2 column, newest first, tags projection) must
/// translate on SQL Server, not only on the in-memory provider the unit tests use.
/// </summary>
[Collection(nameof(TripMateApiFactory))]
public sealed class BehaviorProfileBuilderSqlServerTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 11, 4, 0, 0, TimeSpan.Zero);

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task BuildAsync_ReadsDecayedSignalsWithPoiDetailsFromSqlServer()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        var suffix = Guid.NewGuid().ToString("N");
        var travelerId = await database.ExecuteScalarAsync<long>($"""
            INSERT INTO dbo.Users (role, email, full_name, status)
            OUTPUT INSERTED.user_id
            VALUES ('Traveler', 'profile-{suffix}@example.test', N'Profile', 'Active');
            """);
        var categoryId = await database.ExecuteScalarAsync<int>($"""
            INSERT INTO catalog.POICategories (name)
            OUTPUT INSERTED.category_id
            VALUES (N'Museum-{suffix}');
            """);

        async Task<long> AddPoi(string name) =>
            await database.ExecuteScalarAsync<long>($"""
                INSERT INTO catalog.POIs (category_id, name, latitude, longitude, status, created_by)
                OUTPUT INSERTED.poi_id
                VALUES ({categoryId}, N'{name}-{suffix}', 16.000000, 108.000000, 'Active', {travelerId});
                """);

        var museumId = await AddPoi("Museum");
        var pagodaId = await AddPoi("Pagoda");
        var staleId = await AddPoi("Stale");
        var tagId = await database.ExecuteScalarAsync<int>($"""
            INSERT INTO catalog.Tags (name)
            OUTPUT INSERTED.tag_id
            VALUES (N'history-{suffix}');
            """);
        await database.ExecuteNonQueryAsync($"""
            INSERT INTO catalog.POITagMap (poi_id, tag_id) VALUES ({museumId}, {tagId});
            INSERT INTO social.RecommendationBehaviorEvents (
                traveler_user_id, poi_id, itinerary_id, event_type,
                original_position, new_position, was_mandatory, source, occurred_at_utc, client_event_id)
            VALUES
                ({travelerId}, {museumId}, NULL, 'Like', NULL, NULL, 1, 'SchedulingRequest', '2026-11-11T04:00:00', NEWID()),
                ({travelerId}, {museumId}, NULL, 'Like', NULL, NULL, NULL, 'Explore', '2026-08-13T04:00:00', NEWID()),
                ({travelerId}, {pagodaId}, NULL, 'Dislike', NULL, NULL, NULL, 'PoiDetail', '2026-11-11T04:00:00', NEWID()),
                ({travelerId}, {staleId}, NULL, 'Like', NULL, NULL, NULL, 'Explore', '2025-11-01T04:00:00', NEWID());
            """);
        await using var db = database.CreateDbContext();

        PoiRankingBehavior profile = (await new BehaviorProfileBuilder(
                db,
                new FixedClock(Now),
                new PersonalizationRankingOptions())
            .BuildAsync(travelerId, CancellationToken.None))!;

        // Mandatory today (0.5) + an explicit like 90 days ago (1.0 halved) = 1.0; the year-old like is ignored.
        PoiRankingBehaviorPoi liked = profile.Liked.Should().ContainSingle().Subject;
        liked.PoiId.Should().Be(museumId);
        liked.Weight.Should().Be(1.0m);
        liked.Name.Should().Be($"Museum-{suffix}");
        liked.CategoryName.Should().Be($"Museum-{suffix}");
        liked.TagNames.Should().Equal($"history-{suffix}");
        PoiRankingBehaviorPoi disliked = profile.Disliked.Should().ContainSingle().Subject;
        disliked.PoiId.Should().Be(pagodaId);
        disliked.Weight.Should().Be(1.0m);
    }

    private sealed class FixedClock(DateTimeOffset now) : IDateTimeProvider
    {
        public DateTimeOffset UtcNow { get; } = now;
    }
}