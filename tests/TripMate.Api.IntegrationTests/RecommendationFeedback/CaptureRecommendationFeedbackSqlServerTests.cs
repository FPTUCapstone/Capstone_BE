using System.Net;
using System.Net.Http.Json;

using FluentAssertions;

using Microsoft.Data.SqlClient;

using TripMate.Api.IntegrationTests.Infrastructure;
using TripMate.Application.Features.RecommendationFeedback.Common;
using TripMate.Domain.Enums;

namespace TripMate.Api.IntegrationTests.RecommendationFeedback;

[Collection(nameof(TripMateApiFactory))]
public sealed class CaptureRecommendationFeedbackSqlServerTests
{
    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task Migration_IsIdempotentAndDatabaseChecksRejectInvalidTypeSourceShape()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        await database.ExecuteScriptAsync(MigrationPath());
        var seed = await SeedAsync(database);

        (await database.ExecuteScalarAsync<int>("""
            SELECT COUNT(*) FROM sys.columns
            WHERE object_id = OBJECT_ID(N'social.RecommendationBehaviorEvents');
            """)).Should().Be(11);
        (await database.ExecuteScalarAsync<int>("""
            SELECT COUNT(*) FROM sys.check_constraints
            WHERE parent_object_id = OBJECT_ID(N'social.RecommendationBehaviorEvents')
              AND is_disabled = 0 AND is_not_trusted = 0;
            """)).Should().Be(3);

        Func<Task> adversarialInsert = () => database.ExecuteNonQueryAsync($"""
            INSERT INTO social.RecommendationBehaviorEvents (
                traveler_user_id, poi_id, itinerary_id, event_type,
                original_position, new_position, was_mandatory, source,
                occurred_at_utc, client_event_id)
            VALUES ({seed.OwnerId}, {seed.ActivePoiId}, NULL, 'Skip',
                1, NULL, NULL, 'Explore', SYSUTCDATETIME(), NEWID());
            """);
        await adversarialInsert.Should().ThrowAsync<SqlException>();

        Func<Task> invalidEventType = () => database.ExecuteNonQueryAsync($"""
            INSERT INTO social.RecommendationBehaviorEvents (
                traveler_user_id, poi_id, itinerary_id, event_type,
                original_position, new_position, was_mandatory, source,
                occurred_at_utc, client_event_id)
            VALUES ({seed.OwnerId}, {seed.ActivePoiId}, NULL, 'Unknown',
                NULL, NULL, NULL, 'Explore', SYSUTCDATETIME(), NEWID());
            """);
        Func<Task> invalidSource = () => database.ExecuteNonQueryAsync($"""
            INSERT INTO social.RecommendationBehaviorEvents (
                traveler_user_id, poi_id, itinerary_id, event_type,
                original_position, new_position, was_mandatory, source,
                occurred_at_utc, client_event_id)
            VALUES ({seed.OwnerId}, {seed.ActivePoiId}, NULL, 'Like',
                NULL, NULL, NULL, 'Unknown', SYSUTCDATETIME(), NEWID());
            """);
        await invalidEventType.Should().ThrowAsync<SqlException>();
        await invalidSource.Should().ThrowAsync<SqlException>();
    }

    [SqlServerTheory]
    [Trait("Category", "SqlServer")]
    [InlineData("Like", "Explore")]
    [InlineData("Like", "PoiDetail")]
    [InlineData("Dislike", "Explore")]
    public async Task DirectFeedback_CapturesActivePoi(string eventType, string source)
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        var seed = await SeedAsync(database);
        await using var factory = new TripMateApiFactory(sqlServerConnectionString: database.ConnectionString);
        using var client = factory.CreateAuthenticatedClient(seed.OwnerId, UserRole.Traveler);

        using var response = await client.PostAsJsonAsync(
            "/api/v1/recommendation-feedback",
            Request(Guid.NewGuid(), eventType, seed.ActivePoiId, source));

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        (await database.ExecuteScalarAsync<int>("""
            SELECT COUNT(*) FROM social.RecommendationBehaviorEvents;
            """)).Should().Be(1);
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task ContextualFeedback_EnforcesOwnershipMembershipSkipAndReorderRules()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        var seed = await SeedAsync(database);
        await using var factory = new TripMateApiFactory(sqlServerConnectionString: database.ConnectionString);
        using var owner = factory.CreateAuthenticatedClient(seed.OwnerId, UserRole.Traveler);

        using var contextualLike = await owner.PostAsJsonAsync(
            "/api/v1/recommendation-feedback",
            Request(Guid.NewGuid(), "Like", seed.InactivePoiId, "Itinerary", seed.ItineraryId));
        using var contextualDislike = await owner.PostAsJsonAsync(
            "/api/v1/recommendation-feedback",
            Request(Guid.NewGuid(), "Dislike", seed.ActivePoiId, "Itinerary", seed.ItineraryId));
        using var skip = await owner.PostAsJsonAsync(
            "/api/v1/recommendation-feedback",
            Request(Guid.NewGuid(), "Skip", seed.ActivePoiId, "Itinerary", seed.ItineraryId, 1));
        using var reorder = await owner.PostAsJsonAsync(
            "/api/v1/recommendation-feedback",
            Request(Guid.NewGuid(), "Reorder", seed.ActivePoiId, "Itinerary", seed.ItineraryId, 2, 1));
        using var wrongSkip = await owner.PostAsJsonAsync(
            "/api/v1/recommendation-feedback",
            Request(Guid.NewGuid(), "Skip", seed.ActivePoiId, "Itinerary", seed.ItineraryId, 2));
        using var outOfRange = await owner.PostAsJsonAsync(
            "/api/v1/recommendation-feedback",
            Request(Guid.NewGuid(), "Reorder", seed.ActivePoiId, "Itinerary", seed.ItineraryId, 2, 5));
        using var samePosition = await owner.PostAsJsonAsync(
            "/api/v1/recommendation-feedback",
            Request(Guid.NewGuid(), "Reorder", seed.ActivePoiId, "Itinerary", seed.ItineraryId, 1, 1));
        using var notOwned = await owner.PostAsJsonAsync(
            "/api/v1/recommendation-feedback",
            Request(Guid.NewGuid(), "Like", seed.ActivePoiId, "Itinerary", seed.OtherItineraryId));

        contextualLike.StatusCode.Should().Be(HttpStatusCode.Created);
        contextualDislike.StatusCode.Should().Be(HttpStatusCode.Created);
        skip.StatusCode.Should().Be(HttpStatusCode.Created);
        reorder.StatusCode.Should().Be(HttpStatusCode.Created);
        wrongSkip.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        outOfRange.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        samePosition.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        notOwned.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await database.ExecuteScalarAsync<int>($"""
            SELECT COUNT(*) FROM social.RecommendationBehaviorEvents
            WHERE itinerary_id = {seed.ItineraryId} AND was_mandatory = 1;
            """)).Should().BeGreaterThan(0);
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task AppendValidation_ReturnsExpectedNotFoundAndContextMismatchCodes()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        var seed = await SeedAsync(database);
        await using var factory = new TripMateApiFactory(sqlServerConnectionString: database.ConnectionString);
        using var client = factory.CreateAuthenticatedClient(seed.OwnerId, UserRole.Traveler);

        using var missingPoi = await client.PostAsJsonAsync(
            "/api/v1/recommendation-feedback",
            Request(Guid.NewGuid(), "Like", 999999, "Explore"));
        using var inactiveDirect = await client.PostAsJsonAsync(
            "/api/v1/recommendation-feedback",
            Request(Guid.NewGuid(), "Like", seed.InactivePoiId, "PoiDetail"));
        using var missingItinerary = await client.PostAsJsonAsync(
            "/api/v1/recommendation-feedback",
            Request(Guid.NewGuid(), "Like", seed.ActivePoiId, "Itinerary", 999999));
        using var noMembership = await client.PostAsJsonAsync(
            "/api/v1/recommendation-feedback",
            Request(Guid.NewGuid(), "Like", seed.UnusedPoiId, "Itinerary", seed.ItineraryId));
        using var ambiguous = await client.PostAsJsonAsync(
            "/api/v1/recommendation-feedback",
            Request(Guid.NewGuid(), "Like", seed.DuplicatePoiId, "Itinerary", seed.ItineraryId));

        await AssertFailure(missingPoi, HttpStatusCode.NotFound, FeedbackErrorCodes.PoiNotFound);
        await AssertFailure(inactiveDirect, HttpStatusCode.NotFound, FeedbackErrorCodes.PoiNotFound);
        await AssertFailure(missingItinerary, HttpStatusCode.NotFound, FeedbackErrorCodes.ItineraryNotFound);
        await AssertFailure(noMembership, HttpStatusCode.UnprocessableEntity, FeedbackErrorCodes.ContextMismatch);
        await AssertFailure(ambiguous, HttpStatusCode.UnprocessableEntity, FeedbackErrorCodes.ContextMismatch);
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task IdempotencyLookup_PrecedesMutableResourceValidation()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        var seed = await SeedAsync(database);
        await using var factory = new TripMateApiFactory(sqlServerConnectionString: database.ConnectionString);
        using var client = factory.CreateAuthenticatedClient(seed.OwnerId, UserRole.Traveler);
        var token = Guid.NewGuid();
        var original = Request(token, "Like", seed.ActivePoiId, "Explore");

        using var created = await client.PostAsJsonAsync("/api/v1/recommendation-feedback", original);
        await database.ExecuteNonQueryAsync($"""
            UPDATE catalog.POIs SET status = 'Inactive' WHERE poi_id = {seed.ActivePoiId};
            """);
        using var replay = await client.PostAsJsonAsync("/api/v1/recommendation-feedback", original);
        using var conflict = await client.PostAsJsonAsync(
            "/api/v1/recommendation-feedback",
            Request(token, "Like", 999999, "Explore"));

        created.StatusCode.Should().Be(HttpStatusCode.Created);
        replay.StatusCode.Should().Be(HttpStatusCode.OK);
        await AssertFailure(conflict, HttpStatusCode.Conflict, FeedbackErrorCodes.EventTokenConflict);
        (await database.ExecuteScalarAsync<int>("""
            SELECT COUNT(*) FROM social.RecommendationBehaviorEvents;
            """)).Should().Be(1);
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task ConcurrentIdenticalFirstRequests_CreateExactlyOneRowAndReplayWinner()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        var seed = await SeedAsync(database);
        await using var factory = new TripMateApiFactory(sqlServerConnectionString: database.ConnectionString);
        using var firstClient = factory.CreateAuthenticatedClient(seed.OwnerId, UserRole.Traveler);
        using var secondClient = factory.CreateAuthenticatedClient(seed.OwnerId, UserRole.Traveler);
        var token = Guid.NewGuid();
        var payload = Request(token, "Like", seed.ActivePoiId, "Explore");

        var responses = await Task.WhenAll(
            firstClient.PostAsJsonAsync("/api/v1/recommendation-feedback", payload),
            secondClient.PostAsJsonAsync("/api/v1/recommendation-feedback", payload));
        using var first = responses[0];
        using var second = responses[1];

        responses.Select(response => response.StatusCode).Should().BeEquivalentTo(
            new[] { HttpStatusCode.Created, HttpStatusCode.OK });
        (await database.ExecuteScalarAsync<int>($"""
            SELECT COUNT(*) FROM social.RecommendationBehaviorEvents
            WHERE traveler_user_id = {seed.OwnerId}
              AND client_event_id = '{token:D}';
            """)).Should().Be(1);
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task ConcurrentDivergentTokenReuse_CreatesOneRowAndReturnsConflict()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        var seed = await SeedAsync(database);
        await using var factory = new TripMateApiFactory(sqlServerConnectionString: database.ConnectionString);
        using var firstClient = factory.CreateAuthenticatedClient(seed.OwnerId, UserRole.Traveler);
        using var secondClient = factory.CreateAuthenticatedClient(seed.OwnerId, UserRole.Traveler);
        var token = Guid.NewGuid();

        var responses = await Task.WhenAll(
            firstClient.PostAsJsonAsync(
                "/api/v1/recommendation-feedback",
                Request(token, "Like", seed.ActivePoiId, "Explore")),
            secondClient.PostAsJsonAsync(
                "/api/v1/recommendation-feedback",
                Request(token, "Like", seed.UnusedPoiId, "Explore")));
        using var first = responses[0];
        using var second = responses[1];

        responses.Select(response => response.StatusCode).Should().BeEquivalentTo(
            new[] { HttpStatusCode.Created, HttpStatusCode.Conflict });
        (await database.ExecuteScalarAsync<int>($"""
            SELECT COUNT(*) FROM social.RecommendationBehaviorEvents
            WHERE traveler_user_id = {seed.OwnerId}
              AND client_event_id = '{token:D}';
            """)).Should().Be(1);
    }

    [Fact]
    public async Task Endpoint_RequiresTravelerAuthentication()
    {
        await using var factory = new TripMateApiFactory();
        using var anonymous = factory.CreateClient();
        using var operatorClient = factory.CreateAuthenticatedClient(1, UserRole.TourOperator);
        using var traveler = factory.CreateAuthenticatedClient(1, UserRole.Traveler);
        var payload = Request(Guid.NewGuid(), "Like", 1, "Explore");

        using var unauthorized = await anonymous.PostAsJsonAsync(
            "/api/v1/recommendation-feedback", payload);
        using var forbidden = await operatorClient.PostAsJsonAsync(
            "/api/v1/recommendation-feedback", payload);
        using var invalidToken = await traveler.PostAsJsonAsync(
            "/api/v1/recommendation-feedback",
            Request(Guid.Empty, "Like", 1, "Explore"));

        unauthorized.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        forbidden.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        invalidToken.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    private static object Request(
        Guid clientEventId,
        string eventType,
        long poiId,
        string source,
        long? itineraryId = null,
        int? originalPosition = null,
        int? newPosition = null) => new
        {
            clientEventId,
            eventType,
            poiId,
            itineraryId,
            originalPosition,
            newPosition,
            source,
        };

    private static async Task AssertFailure(
        HttpResponseMessage response,
        HttpStatusCode expectedStatus,
        string expectedCode)
    {
        response.StatusCode.Should().Be(expectedStatus);
        (await response.Content.ReadAsStringAsync()).Should().Contain(expectedCode);
    }

    private static async Task<Seed> SeedAsync(SqlServerTestDatabase database)
    {
        var suffix = Guid.NewGuid().ToString("N");
        var ownerId = await database.ExecuteScalarAsync<long>($"""
            INSERT INTO dbo.Users (role, email, full_name, status)
            OUTPUT INSERTED.user_id
            VALUES ('Traveler', 'owner-{suffix}@example.test', N'Owner', 'Active');
            """);
        var otherId = await database.ExecuteScalarAsync<long>($"""
            INSERT INTO dbo.Users (role, email, full_name, status)
            OUTPUT INSERTED.user_id
            VALUES ('Traveler', 'other-{suffix}@example.test', N'Other', 'Active');
            """);
        var categoryId = await database.ExecuteScalarAsync<int>($"""
            INSERT INTO catalog.POICategories (name)
            OUTPUT INSERTED.category_id
            VALUES (N'Category-{suffix}');
            """);

        async Task<long> AddPoi(string name, string status) =>
            await database.ExecuteScalarAsync<long>($"""
                INSERT INTO catalog.POIs (
                    category_id, name, latitude, longitude, status, created_by)
                OUTPUT INSERTED.poi_id
                VALUES ({categoryId}, N'{name}-{suffix}', 10.000000, 106.000000,
                    '{status}', {ownerId});
                """);

        var activePoiId = await AddPoi("Active", "Active");
        var inactivePoiId = await AddPoi("Inactive", "Inactive");
        var unusedPoiId = await AddPoi("Unused", "Active");
        var duplicatePoiId = await AddPoi("Duplicate", "Active");
        var itineraryId = await database.ExecuteScalarAsync<long>($"""
            INSERT INTO planning.Itineraries (traveler_user_id, source_type, title)
            OUTPUT INSERTED.itinerary_id
            VALUES ({ownerId}, 'Manual', N'Owner itinerary');
            """);
        var otherItineraryId = await database.ExecuteScalarAsync<long>($"""
            INSERT INTO planning.Itineraries (traveler_user_id, source_type, title)
            OUTPUT INSERTED.itinerary_id
            VALUES ({otherId}, 'Manual', N'Other itinerary');
            """);
        await database.ExecuteNonQueryAsync($"""
            INSERT INTO planning.ItineraryItems (
                itinerary_id, sequence_no, poi_id, stay_duration_minutes,
                item_kind, is_mandatory)
            VALUES
                ({itineraryId}, 1, {activePoiId}, 60, 'Visit', 1),
                ({itineraryId}, 2, {inactivePoiId}, 60, 'Visit', 0),
                ({itineraryId}, 3, {duplicatePoiId}, 60, 'Visit', 0),
                ({itineraryId}, 4, {duplicatePoiId}, 60, 'Visit', 1),
                ({otherItineraryId}, 1, {activePoiId}, 60, 'Visit', 0);
            """);

        return new Seed(
            ownerId,
            activePoiId,
            inactivePoiId,
            unusedPoiId,
            duplicatePoiId,
            itineraryId,
            otherItineraryId);
    }

    private static string MigrationPath()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "TripMate.slnx")))
        {
            directory = directory.Parent;
        }

        return Path.Combine(
            directory?.FullName ?? throw new InvalidOperationException("Repository root not found."),
            "database",
            "migrations",
            "20260929_add_recommendation_behavior_events.sql");
    }

    private sealed record Seed(
        long OwnerId,
        long ActivePoiId,
        long InactivePoiId,
        long UnusedPoiId,
        long DuplicatePoiId,
        long ItineraryId,
        long OtherItineraryId);
}