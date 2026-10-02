using FluentAssertions;

namespace TripMate.Api.IntegrationTests.Infrastructure;

public sealed class ItineraryFriendlyExplanationMigrationSqlServerTests
{
    private const string MigrationFileName =
        "20261001_add_itinerary_item_friendly_explanation.sql";

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task FreshCurrentSchema_MigrationAddsNullableNvarchar500Column()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();

        var column = await ReadFriendlyExplanationColumnAsync(database);

        column.Should().Be(new ColumnDefinition("nvarchar", 500, IsNullable: true));
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task ExistingRow_MigrationPreservesRowWithNullFriendlyExplanation()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        await database.ExecuteNonQueryAsync("""
            ALTER TABLE planning.ItineraryItems DROP COLUMN friendly_explanation;

            INSERT dbo.Users(role, email, full_name)
            VALUES ('Traveler', N'tm217-legacy@example.com', N'TM-217 Legacy Traveler');

            DECLARE @travelerId BIGINT = SCOPE_IDENTITY();

            INSERT planning.Itineraries(traveler_user_id, source_type, title)
            VALUES (@travelerId, 'Manual', N'TM-217 Legacy Itinerary');

            DECLARE @itineraryId BIGINT = SCOPE_IDENTITY();

            INSERT planning.ItineraryItems(
                itinerary_id,
                sequence_no,
                planned_arrival,
                planned_departure,
                stay_duration_minutes,
                item_kind,
                is_mandatory,
                recommendation_reason)
            VALUES (
                @itineraryId,
                1,
                '2026-10-01T01:00:00',
                '2026-10-01T01:30:00',
                30,
                'Rest',
                0,
                N'Legacy rest');
            """);

        await database.ExecuteScriptAsync(GetMigrationPath());

        var rowCount = await database.ExecuteScalarAsync<int>("""
            SELECT COUNT(*)
            FROM planning.ItineraryItems
            WHERE recommendation_reason = N'Legacy rest'
              AND friendly_explanation IS NULL;
            """);
        rowCount.Should().Be(1);
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task Migration_AppliedTwice_SucceedsAndKeepsSingleColumn()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        await database.ExecuteNonQueryAsync(
            "ALTER TABLE planning.ItineraryItems DROP COLUMN friendly_explanation;");

        await database.ExecuteScriptAsync(GetMigrationPath());
        await database.ExecuteScriptAsync(GetMigrationPath());

        var columnCount = await database.ExecuteScalarAsync<int>("""
            SELECT COUNT(*)
            FROM sys.columns
            WHERE object_id = OBJECT_ID(N'planning.ItineraryItems')
              AND name = N'friendly_explanation';
            """);
        columnCount.Should().Be(1);
        (await ReadFriendlyExplanationColumnAsync(database)).Should().Be(
            new ColumnDefinition("nvarchar", 500, IsNullable: true));
    }

    private static async Task<ColumnDefinition?> ReadFriendlyExplanationColumnAsync(
        SqlServerTestDatabase database)
    {
        const string commandText = """
            SELECT
                type_info.name,
                column_info.max_length / 2,
                CONVERT(bit, column_info.is_nullable)
            FROM sys.columns AS column_info
            INNER JOIN sys.types AS type_info
                ON type_info.user_type_id = column_info.user_type_id
            WHERE column_info.object_id = OBJECT_ID(N'planning.ItineraryItems')
              AND column_info.name = N'friendly_explanation';
            """;

        await using var connection = new Microsoft.Data.SqlClient.SqlConnection(
            database.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = commandText;
        await using var reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
        {
            return null;
        }

        return new ColumnDefinition(
            reader.GetString(0),
            reader.GetInt32(1),
            reader.GetBoolean(2));
    }

    private static string GetMigrationPath() =>
        Path.Combine(AppContext.BaseDirectory, "Database", "migrations", MigrationFileName);

    private sealed record ColumnDefinition(string TypeName, int MaxLength, bool IsNullable);
}