using FluentAssertions;

using Microsoft.Data.SqlClient;

namespace TripMate.Api.IntegrationTests.Infrastructure;

public sealed class NavigationExecutionMigrationSqlServerTests
{
    private const string MigrationFileName = "20261008_add_uc13_navigation_execution.sql";
    private const string Revision1FixtureFileName = "uc13_revision1_navigation_execution.sql";

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task LegacyRows_MigrationBackfillsDistinctKeysAndCanRunTwice()
    {
        await using var database = await SqlServerTestDatabase.CreateEmptyAsync();
        await ApplySchemaV7Async(database);
        await database.ExecuteNonQueryAsync("""
            INSERT dbo.Users(role, email, full_name)
            VALUES ('Traveler', N'uc13-legacy@example.com', N'UC-13 Legacy');
            DECLARE @travelerId BIGINT = SCOPE_IDENTITY();

            INSERT planning.Itineraries(traveler_user_id, source_type, title, status)
            VALUES (@travelerId, 'Manual', N'UC-13 Legacy', 'Active');
            DECLARE @itineraryId BIGINT = SCOPE_IDENTITY();

            INSERT trip.TripSessions(itinerary_id, traveler_user_id, fsm_state, started_at, ended_at)
            VALUES
                (@itineraryId, @travelerId, 'Completed', SYSUTCDATETIME(), SYSUTCDATETIME()),
                (@itineraryId, @travelerId, 'Exploring', SYSUTCDATETIME(), NULL);
            """);

        var migration = await File.ReadAllTextAsync(GetMigrationPath());
        await database.ExecuteNonQueryAsync($"""
            SET ANSI_NULLS OFF;
            SET ANSI_PADDING OFF;
            SET ANSI_WARNINGS OFF;
            SET ARITHABORT OFF;
            SET CONCAT_NULL_YIELDS_NULL OFF;
            SET QUOTED_IDENTIFIER OFF;
            SET NUMERIC_ROUNDABORT ON;
            {migration}
            """);
        await database.ExecuteScriptAsync(GetMigrationPath());

        (await database.ExecuteScalarAsync<int>("""
            SELECT COUNT(DISTINCT start_idempotency_key)
            FROM trip.TripSessions;
            """)).Should().Be(2);
        (await database.ExecuteScalarAsync<int>("""
            SELECT COUNT(*)
            FROM trip.TripSessions
            WHERE requested_itinerary_id = itinerary_id
              AND start_idempotency_key IS NOT NULL
              AND row_version IS NOT NULL
              AND expires_at IS NULL
              AND exploring_item_id IS NULL;
            """)).Should().Be(2);
        (await database.ExecuteScalarAsync<int>("""
            SELECT COUNT(*)
            FROM sys.default_constraints AS defaults
            INNER JOIN sys.columns AS columns
                ON columns.object_id = defaults.parent_object_id
               AND columns.column_id = defaults.parent_column_id
            WHERE defaults.parent_object_id = OBJECT_ID(N'trip.TripSessions')
              AND columns.name = N'start_idempotency_key';
            """)).Should().Be(0);
        (await database.ExecuteScalarAsync<int>("""
            SELECT COUNT(*)
            FROM sys.indexes
            WHERE object_id = OBJECT_ID(N'trip.TripSessions')
              AND name = N'UQ_TripSessions_OpenTraveler'
              AND is_unique = 1
              AND has_filter = 1
              AND filter_definition LIKE N'%ended_at%IS NULL%';
            """)).Should().Be(1);
        await AssertCurrentContractAsync(database);
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task Revision1Shape_UpgradesInPlaceAndBackfillsSnapshotSchedule()
    {
        await using var database = await SqlServerTestDatabase.CreateEmptyAsync();
        await ApplySchemaV7Async(database);
        await database.ExecuteScriptAsync(GetRevision1FixturePath());
        await SeedRevision1SessionAsync(database, "TravelerStopped");

        await database.ExecuteScriptAsync(GetMigrationPath());
        await database.ExecuteScriptAsync(GetMigrationPath());

        (await database.ExecuteScalarAsync<int>("""
            SELECT COUNT(*)
            FROM trip.TripSessionItems AS snapshot
            INNER JOIN planning.ItineraryItems AS source
                ON source.item_id = snapshot.itinerary_item_id
            WHERE snapshot.planned_arrival = source.planned_arrival
              AND snapshot.planned_departure = source.planned_departure
              AND snapshot.is_mandatory = source.is_mandatory
              AND snapshot.is_mandatory = 1
              AND snapshot.reached_at IS NOT NULL
              AND snapshot.skipped_at IS NULL;
            """)).Should().Be(1);
        await AssertCurrentContractAsync(database);
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task Revision1Shape_WithRemovedCompletionReason_FailsWithoutPartialChanges()
    {
        await using var database = await SqlServerTestDatabase.CreateEmptyAsync();
        await ApplySchemaV7Async(database);
        await database.ExecuteScriptAsync(GetRevision1FixturePath());
        await SeedRevision1SessionAsync(database, "AllItemsReached");

        Func<Task> migrate = () => database.ExecuteScriptAsync(GetMigrationPath());

        (await migrate.Should().ThrowAsync<SqlException>())
            .Which.Message.Should().Contain("AllItemsReached");
        (await database.ExecuteScalarAsync<int>("""
            SELECT COUNT(*)
            FROM sys.columns
            WHERE object_id IN (OBJECT_ID(N'trip.TripSessions'), OBJECT_ID(N'trip.TripSessionItems'))
              AND name IN (N'expires_at', N'exploring_item_id', N'planned_arrival', N'skipped_at');
            """)).Should().Be(0);
    }

    private static async Task AssertCurrentContractAsync(SqlServerTestDatabase database)
    {
        (await database.ExecuteScalarAsync<int>("""
            SELECT COUNT(*)
            FROM (VALUES
                (N'trip.TripSessions', N'expires_at', N'datetime2', 1),
                (N'trip.TripSessions', N'exploring_item_id', N'bigint', 1),
                (N'trip.TripSessionItems', N'planned_arrival', N'datetime2', 0),
                (N'trip.TripSessionItems', N'planned_departure', N'datetime2', 0),
                (N'trip.TripSessionItems', N'is_mandatory', N'bit', 0),
                (N'trip.TripSessionItems', N'reached_at', N'datetime2', 1),
                (N'trip.TripSessionItems', N'skipped_at', N'datetime2', 1)
            ) AS expected(table_name, column_name, type_name, is_nullable)
            INNER JOIN sys.columns AS columns
                ON columns.object_id = OBJECT_ID(expected.table_name)
               AND columns.name = expected.column_name
            INNER JOIN sys.types AS types
                ON types.user_type_id = columns.user_type_id
            WHERE types.name = expected.type_name
              AND columns.is_nullable = expected.is_nullable;
            """)).Should().Be(7);

        (await database.ExecuteScalarAsync<int>("""
            SELECT COUNT(*)
            FROM sys.check_constraints
            WHERE parent_object_id = OBJECT_ID(N'trip.TripSessions')
              AND name IN (N'CK_TripSessions_CompletionReason', N'CK_TripSessions_ExploringItem')
              AND is_disabled = 0
              AND is_not_trusted = 0;
            """)).Should().Be(2);

        await database.ExecuteNonQueryAsync("""
            BEGIN TRANSACTION;
            UPDATE TOP (1) trip.TripSessions
            SET fsm_state = 'Completed', completion_reason = 'Expired', ended_at = SYSUTCDATETIME();
            ROLLBACK TRANSACTION;
            """);
        Func<Task> removedReason = () => database.ExecuteNonQueryAsync("""
            UPDATE TOP (1) trip.TripSessions SET completion_reason = 'AllItemsReached';
            """);
        await removedReason.Should().ThrowAsync<SqlException>();
        Func<Task> exploringItemOutsideExploring = () => database.ExecuteNonQueryAsync("""
            UPDATE TOP (1) trip.TripSessions
            SET fsm_state = 'Navigating', exploring_item_id = 1;
            """);
        await exploringItemOutsideExploring.Should().ThrowAsync<SqlException>();
    }

    private static async Task SeedRevision1SessionAsync(
        SqlServerTestDatabase database,
        string completionReason)
    {
        await database.ExecuteNonQueryAsync($"""
            INSERT dbo.Users(role, email, full_name)
            VALUES ('Traveler', N'uc13-revision1@example.com', N'UC-13 Revision 1');
            DECLARE @travelerId BIGINT = SCOPE_IDENTITY();

            INSERT catalog.POICategories(name) VALUES (N'UC-13 revision 1');
            DECLARE @categoryId INT = SCOPE_IDENTITY();
            INSERT catalog.POIs(category_id, name, latitude, longitude)
            VALUES (@categoryId, N'Dragon Bridge', 16.061100, 108.227600);
            DECLARE @poiId BIGINT = SCOPE_IDENTITY();

            INSERT planning.Itineraries(traveler_user_id, source_type, title, status)
            VALUES (@travelerId, 'Manual', N'UC-13 Revision 1', 'Active');
            DECLARE @itineraryId BIGINT = SCOPE_IDENTITY();
            INSERT planning.ItineraryItems(
                itinerary_id, sequence_no, poi_id, planned_arrival, planned_departure, is_mandatory)
            VALUES (
                @itineraryId, 1, @poiId, '2026-10-20T02:00:00', '2026-10-20T03:30:00', 1);
            DECLARE @itemId BIGINT = SCOPE_IDENTITY();

            INSERT trip.TripSessions(
                itinerary_id, requested_itinerary_id, traveler_user_id, start_idempotency_key,
                fsm_state, completion_reason, started_at, ended_at)
            VALUES (
                @itineraryId, @itineraryId, @travelerId, NEWID(),
                'Completed', '{completionReason}', '2026-10-20T01:30:00', '2026-10-20T02:15:00');
            DECLARE @sessionId BIGINT = SCOPE_IDENTITY();

            INSERT trip.TripSessionItems(
                session_id, itinerary_item_id, sequence_no, poi_id, poi_name, latitude, longitude, reached_at)
            VALUES (
                @sessionId, @itemId, 1, @poiId, N'Dragon Bridge', 16.061100, 108.227600, '2026-10-20T02:15:00');
            """);
    }

    private static Task ApplySchemaV7Async(SqlServerTestDatabase database) =>
        database.ExecuteScriptAsync(Path.Combine(
            AppContext.BaseDirectory,
            "Database",
            "tripmate_schema_v7.sql"));

    private static string GetMigrationPath() =>
        Path.Combine(AppContext.BaseDirectory, "Database", "migrations", MigrationFileName);

    private static string GetRevision1FixturePath() =>
        Path.Combine(AppContext.BaseDirectory, "Database", "Fixtures", Revision1FixtureFileName);
}