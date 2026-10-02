using FluentAssertions;

using TripMate.Api.IntegrationTests.Infrastructure;
using TripMate.Application.Features.Scheduling.Personalization;

namespace TripMate.Api.IntegrationTests.Scheduling;

public sealed class PersonalBehaviorFeatureAggregatorSqlServerTests
{
    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task AggregateAsync_WithMixedHistory_ReturnsScopedCountsInTwoQueries()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        AggregationSeed seed = await SeedAsync(database);
        var interceptor = new TestCommandCounterInterceptor();
        await using var context = database.CreateDbContext(interceptor);
        var aggregator = new PersonalBehaviorFeatureAggregator(context);

        PersonalBehaviorAggregation result = await aggregator.AggregateAsync(
            seed.TravelerUserId,
            [seed.CandidatePoiId, seed.CandidatePoiId],
            [seed.RelevantCategoryId, seed.RelevantCategoryId],
            CancellationToken.None);

        result.PoiCounts.Should().ContainSingle();
        result.PoiCounts[seed.CandidatePoiId].Should().Be(
            new PersonalBehaviorCounts(LikeCount: 2, DislikeCount: 1, SkipCount: 1));
        result.PoiCounts.Should().NotContainKey(seed.NonCandidatePoiId);
        result.CategoryCounts.Should().ContainSingle();
        result.CategoryCounts[seed.RelevantCategoryId].Should().Be(
            new CategoryBehaviorCounts(
                LikeCount: 3,
                DislikeCount: 2,
                SkipCount: 2,
                DistinctInteractedPoiCount: 3));
        interceptor.CommandCount.Should().Be(2);

        interceptor.Reset();
        PersonalBehaviorAggregation expanded = await aggregator.AggregateAsync(
            seed.TravelerUserId,
            [seed.CandidatePoiId, seed.NonCandidatePoiId],
            [seed.RelevantCategoryId, seed.IrrelevantCategoryId],
            CancellationToken.None);

        expanded.CategoryCounts[seed.IrrelevantCategoryId]
            .DistinctInteractedPoiCount.Should().Be(2);
        interceptor.CommandCount.Should().Be(2,
            "candidate and category growth must not create per-item queries");
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task AggregateAsync_WithEmptyAndMissingInputs_SkipsUnavailableQueryPaths()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        AggregationSeed seed = await SeedAsync(database);
        var interceptor = new TestCommandCounterInterceptor();
        await using var context = database.CreateDbContext(interceptor);
        var aggregator = new PersonalBehaviorFeatureAggregator(context);

        PersonalBehaviorAggregation categoryOnly = await aggregator.AggregateAsync(
            seed.TravelerUserId,
            [],
            [seed.RelevantCategoryId],
            CancellationToken.None);

        categoryOnly.PoiCounts.Should().BeEmpty();
        categoryOnly.CategoryCounts.Should().ContainKey(seed.RelevantCategoryId);
        interceptor.CommandCount.Should().Be(1);

        interceptor.Reset();
        PersonalBehaviorAggregation poiOnly = await aggregator.AggregateAsync(
            seed.TravelerUserId,
            [seed.CandidatePoiId],
            [],
            CancellationToken.None);

        poiOnly.PoiCounts.Should().ContainKey(seed.CandidatePoiId);
        poiOnly.CategoryCounts.Should().BeEmpty();
        interceptor.CommandCount.Should().Be(1);

        interceptor.Reset();
        PersonalBehaviorAggregation bothEmpty = await aggregator.AggregateAsync(
            seed.TravelerUserId,
            [],
            [],
            CancellationToken.None);

        bothEmpty.PoiCounts.Should().BeEmpty();
        bothEmpty.CategoryCounts.Should().BeEmpty();
        interceptor.CommandCount.Should().Be(0);

        interceptor.Reset();
        PersonalBehaviorAggregation missing = await aggregator.AggregateAsync(
            seed.TravelerUserId,
            [long.MaxValue],
            [int.MaxValue],
            CancellationToken.None);

        missing.PoiCounts.Should().BeEmpty();
        missing.CategoryCounts.Should().BeEmpty();
        interceptor.CommandCount.Should().Be(2);
    }

    private static async Task<AggregationSeed> SeedAsync(SqlServerTestDatabase database)
    {
        var suffix = Guid.NewGuid().ToString("N");
        var travelerUserId = await AddTravelerAsync(database, $"traveler-{suffix}");
        var otherTravelerUserId = await AddTravelerAsync(database, $"other-{suffix}");
        var relevantCategoryId = await AddCategoryAsync(database, $"Relevant-{suffix}");
        var previousCategoryId = await AddCategoryAsync(database, $"Previous-{suffix}");
        var irrelevantCategoryId = await AddCategoryAsync(database, $"Irrelevant-{suffix}");
        var candidatePoiId = await AddPoiAsync(
            database,
            relevantCategoryId,
            travelerUserId,
            $"Candidate-{suffix}");
        var nonCandidatePoiId = await AddPoiAsync(
            database,
            relevantCategoryId,
            travelerUserId,
            $"Historical-{suffix}");
        var movedPoiId = await AddPoiAsync(
            database,
            previousCategoryId,
            travelerUserId,
            $"Moved-{suffix}");
        var irrelevantPoiId = await AddPoiAsync(
            database,
            irrelevantCategoryId,
            travelerUserId,
            $"Irrelevant-{suffix}");
        var secondIrrelevantPoiId = await AddPoiAsync(
            database,
            irrelevantCategoryId,
            travelerUserId,
            $"Second-Irrelevant-{suffix}");
        var reorderOnlyPoiId = await AddPoiAsync(
            database,
            relevantCategoryId,
            travelerUserId,
            $"Reorder-{suffix}");
        var itineraryId = await database.ExecuteScalarAsync<long>($"""
            INSERT INTO planning.Itineraries (traveler_user_id, source_type, title)
            OUTPUT INSERTED.itinerary_id
            VALUES ({travelerUserId}, 'Manual', N'Behavior aggregation {suffix}');
            """);

        await database.ExecuteNonQueryAsync($"""
            INSERT INTO social.RecommendationBehaviorEvents (
                traveler_user_id, poi_id, itinerary_id, event_type,
                original_position, new_position, was_mandatory, source,
                occurred_at_utc, client_event_id)
            VALUES
                ({travelerUserId}, {candidatePoiId}, NULL, 'Like', NULL, NULL, NULL,
                    'Explore', SYSUTCDATETIME(), NEWID()),
                ({travelerUserId}, {candidatePoiId}, NULL, 'Like', NULL, NULL, NULL,
                    'Explore', SYSUTCDATETIME(), NEWID()),
                ({travelerUserId}, {candidatePoiId}, NULL, 'Dislike', NULL, NULL, NULL,
                    'Explore', SYSUTCDATETIME(), NEWID()),
                ({travelerUserId}, {candidatePoiId}, {itineraryId}, 'Skip', 1, NULL, 0,
                    'Itinerary', SYSUTCDATETIME(), NEWID()),
                ({travelerUserId}, {candidatePoiId}, {itineraryId}, 'Reorder', 1, 2, 0,
                    'Itinerary', SYSUTCDATETIME(), NEWID()),
                ({otherTravelerUserId}, {candidatePoiId}, NULL, 'Like', NULL, NULL, NULL,
                    'Explore', SYSUTCDATETIME(), NEWID()),
                ({travelerUserId}, {nonCandidatePoiId}, NULL, 'Dislike', NULL, NULL, NULL,
                    'Explore', SYSUTCDATETIME(), NEWID()),
                ({travelerUserId}, {nonCandidatePoiId}, {itineraryId}, 'Skip', 2, NULL, 0,
                    'Itinerary', SYSUTCDATETIME(), NEWID()),
                ({travelerUserId}, {movedPoiId}, NULL, 'Like', NULL, NULL, NULL,
                    'Explore', SYSUTCDATETIME(), NEWID()),
                ({travelerUserId}, {irrelevantPoiId}, NULL, 'Like', NULL, NULL, NULL,
                    'Explore', SYSUTCDATETIME(), NEWID()),
                ({travelerUserId}, {secondIrrelevantPoiId}, NULL, 'Dislike', NULL, NULL, NULL,
                    'Explore', SYSUTCDATETIME(), NEWID()),
                ({travelerUserId}, {reorderOnlyPoiId}, {itineraryId}, 'Reorder', 1, 2, 0,
                    'Itinerary', SYSUTCDATETIME(), NEWID());

            UPDATE catalog.POIs
            SET category_id = {relevantCategoryId}
            WHERE poi_id = {movedPoiId};
            """);

        return new AggregationSeed(
            travelerUserId,
            relevantCategoryId,
            irrelevantCategoryId,
            candidatePoiId,
            nonCandidatePoiId);
    }

    private static Task<long> AddTravelerAsync(SqlServerTestDatabase database, string name) =>
        database.ExecuteScalarAsync<long>($"""
            INSERT INTO dbo.Users (role, email, full_name, status)
            OUTPUT INSERTED.user_id
            VALUES ('Traveler', '{name}@example.test', N'{name}', 'Active');
            """);

    private static Task<int> AddCategoryAsync(SqlServerTestDatabase database, string name) =>
        database.ExecuteScalarAsync<int>($"""
            INSERT INTO catalog.POICategories (name)
            OUTPUT INSERTED.category_id
            VALUES (N'{name}');
            """);

    private static Task<long> AddPoiAsync(
        SqlServerTestDatabase database,
        int categoryId,
        long createdById,
        string name) => database.ExecuteScalarAsync<long>($"""
            INSERT INTO catalog.POIs (
                category_id, name, latitude, longitude, status, created_by)
            OUTPUT INSERTED.poi_id
            VALUES ({categoryId}, N'{name}', 10.000000, 106.000000, 'Active', {createdById});
            """);

    private sealed record AggregationSeed(
        long TravelerUserId,
        int RelevantCategoryId,
        int IrrelevantCategoryId,
        long CandidatePoiId,
        long NonCandidatePoiId);
}