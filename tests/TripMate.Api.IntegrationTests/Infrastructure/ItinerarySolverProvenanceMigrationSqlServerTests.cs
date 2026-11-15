using FluentAssertions;

namespace TripMate.Api.IntegrationTests.Infrastructure;

/// <summary>ADR-002 (G2): itinerary provenance columns are additive, nullable and safe to rerun.</summary>
public sealed class ItinerarySolverProvenanceMigrationSqlServerTests
{
    private const string MigrationFileName = "20261110_uc10_itinerary_solver_provenance.sql";

    private const string ExpectedColumns =
        "objective_mode:varchar:20:1,search_completed:bit:1:1,solver_outcome:varchar:64:1,time_of_day_refined:bit:1:1";

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task FreshCurrentSchema_HasTheNullableProvenanceColumns()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();

        (await ReadProvenanceColumnsAsync(database)).Should().Be(ExpectedColumns);
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task ExistingItinerary_KeepsItsRowWithNullProvenance_AndTheMigrationIsSafeToRerun()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        await database.ExecuteNonQueryAsync("""
            ALTER TABLE planning.Itineraries
                DROP COLUMN solver_outcome, objective_mode, search_completed, time_of_day_refined;

            INSERT dbo.Users(role, email, full_name)
            VALUES ('Traveler', N'uc10-provenance-legacy@example.com', N'UC10 Legacy Traveler');

            DECLARE @travelerId BIGINT = SCOPE_IDENTITY();

            INSERT planning.Itineraries(traveler_user_id, source_type, title)
            VALUES (@travelerId, 'Manual', N'UC10 legacy itinerary');
            """);

        await database.ExecuteScriptAsync(GetMigrationPath());
        await database.ExecuteScriptAsync(GetMigrationPath());

        (await ReadProvenanceColumnsAsync(database)).Should().Be(ExpectedColumns);
        (await database.ExecuteScalarAsync<int>("""
            SELECT COUNT(*)
            FROM planning.Itineraries
            WHERE title = N'UC10 legacy itinerary'
              AND solver_outcome IS NULL
              AND objective_mode IS NULL
              AND search_completed IS NULL
              AND time_of_day_refined IS NULL;
            """)).Should().Be(1);
    }

    private static Task<string> ReadProvenanceColumnsAsync(SqlServerTestDatabase database) =>
        database.ExecuteScalarAsync<string>("""
            SELECT STRING_AGG(
                    CONCAT(column_info.name, ':', type_info.name, ':', column_info.max_length, ':', CONVERT(int, column_info.is_nullable)),
                    ',') WITHIN GROUP (ORDER BY column_info.name)
            FROM sys.columns AS column_info
            INNER JOIN sys.types AS type_info
                ON type_info.user_type_id = column_info.user_type_id
            WHERE column_info.object_id = OBJECT_ID(N'planning.Itineraries')
              AND column_info.name IN (N'solver_outcome', N'objective_mode', N'search_completed', N'time_of_day_refined');
            """);

    private static string GetMigrationPath() =>
        Path.Combine(AppContext.BaseDirectory, "Database", "migrations", MigrationFileName);
}