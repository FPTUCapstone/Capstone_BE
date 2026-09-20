using FluentAssertions;

using Microsoft.Data.SqlClient;

using TripMate.Api.IntegrationTests.Infrastructure;

namespace TripMate.Api.IntegrationTests.Tours;

public sealed class TourSearchMigrationTests
{
    private const string MigrationFileName = "20260915_add_tour_search_fields.sql";

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task FreshSchema_HasTourDestinationsAndTrustedWholeVndConstraint()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();

        await AssertTargetShapeAsync(database);
        await ApplyMigrationAsync(database);
        await AssertTargetShapeAsync(database);
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task Migration_FromRecordedBaseline_IsIdempotentAndPreservesDataIdentityAndForeignKeys()
    {
        await using var database = await CreateOldSchemaDatabaseAsync();
        await database.ExecuteNonQueryAsync("""
            INSERT INTO dbo.OperatorProfiles (user_id) VALUES (41);
            SET IDENTITY_INSERT commerce.Tours ON;
            INSERT INTO commerce.Tours
                (tour_id, operator_user_id, title, base_price, duration_days, status)
            VALUES
                (9007199254740993, 41, N'Legacy whole-VND tour', 1250000.00, 3, 'Approved');
            SET IDENTITY_INSERT commerce.Tours OFF;

            INSERT INTO commerce.TourSchedules
                (tour_id, start_datetime, end_datetime, total_capacity, reserved_capacity)
            VALUES
                (9007199254740993, '2026-09-20T01:00:00', '2026-09-23T01:00:00', 12, 2);
            """);

        await ApplyMigrationAsync(database);
        await ApplyMigrationAsync(database);

        await AssertTargetShapeAsync(database);
        await database.ExecuteNonQueryAsync("""
            IF NOT EXISTS (
                SELECT 1
                FROM commerce.Tours
                WHERE tour_id = 9007199254740993
                  AND operator_user_id = 41
                  AND title = N'Legacy whole-VND tour'
                  AND base_price = 1250000.00)
                THROW 51000, 'Migration changed the legacy tour row.', 1;

            IF EXISTS (SELECT 1 FROM commerce.TourDestinations WHERE tour_id = 9007199254740993)
                THROW 51000, 'Migration guessed destinations for the legacy tour.', 1;

            IF NOT EXISTS (
                SELECT 1
                FROM commerce.TourSchedules
                WHERE tour_id = 9007199254740993
                  AND total_capacity = 12
                  AND reserved_capacity = 2)
                THROW 51000, 'Migration changed or orphaned the child schedule.', 1;

            IF IDENT_CURRENT(N'commerce.Tours') <> 9007199254740993
                THROW 51000, 'Migration changed the Tours identity value.', 1;

            IF NOT EXISTS (
                SELECT 1
                FROM sys.foreign_keys
                WHERE parent_object_id = OBJECT_ID(N'commerce.TourSchedules')
                  AND referenced_object_id = OBJECT_ID(N'commerce.Tours')
                  AND is_disabled = 0
                  AND is_not_trusted = 0)
                THROW 51000, 'Migration changed the TourSchedules foreign key.', 1;
            """);
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task Migration_WhenFractionalPriceExists_RejectsWithoutPartialChanges()
    {
        await using var database = await CreateOldSchemaDatabaseAsync();
        await database.ExecuteNonQueryAsync("""
            INSERT INTO dbo.OperatorProfiles (user_id) VALUES (42);
            INSERT INTO commerce.Tours
                (operator_user_id, title, base_price, duration_days, status)
            VALUES
                (42, N'Invalid fractional price', 123.45, 1, 'Draft');
            """);

        Func<Task> action = () => ApplyMigrationAsync(database);

        await action.Should()
            .ThrowAsync<SqlException>()
            .WithMessage("*whole-VND*");
        await database.ExecuteNonQueryAsync("""
            IF OBJECT_ID(N'catalog.Destinations', N'U') IS NOT NULL
                THROW 51000, 'Failed migration left Destinations behind.', 1;
            IF OBJECT_ID(N'commerce.TourDestinations', N'U') IS NOT NULL
                THROW 51000, 'Failed migration left TourDestinations behind.', 1;

            IF OBJECT_ID(N'commerce.CK_Tours_BasePriceWholeVnd', N'C') IS NOT NULL
                THROW 51000, 'Failed migration left the constraint behind.', 1;

            IF NOT EXISTS (
                SELECT 1 FROM commerce.Tours WHERE base_price = 123.45)
                THROW 51000, 'Failed migration changed fractional data.', 1;
            """);
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task Migration_WhenDestinationTableHasWrongShape_RejectsWithoutAddingConstraint()
    {
        await using var database = await CreateOldSchemaDatabaseAsync();
        await database.ExecuteNonQueryAsync(
            "EXEC(N'CREATE SCHEMA catalog'); CREATE TABLE catalog.Destinations (destination_id INT NOT NULL PRIMARY KEY);");

        Func<Task> action = () => ApplyMigrationAsync(database);

        await action.Should()
            .ThrowAsync<SqlException>()
            .WithMessage("*Destinations*shape mismatch*");
        await database.ExecuteNonQueryAsync("""
            IF OBJECT_ID(N'commerce.CK_Tours_BasePriceWholeVnd', N'C') IS NOT NULL
                THROW 51000, 'Shape mismatch left a partial constraint behind.', 1;
            """);
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task Migration_WhenNamedConstraintHasWrongDefinition_RejectsIt()
    {
        await using var database = await CreateOldSchemaDatabaseAsync();
        await database.ExecuteNonQueryAsync("""
            ALTER TABLE commerce.Tours
            ADD CONSTRAINT CK_Tours_BasePriceWholeVnd CHECK (base_price >= 0);
            """);

        Func<Task> action = () => ApplyMigrationAsync(database);

        await action.Should()
            .ThrowAsync<SqlException>()
            .WithMessage("*constraint definition mismatch*");
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task Migration_WhenExistingSequenceIndexHasWrongKeys_RejectsIt()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        await database.ExecuteNonQueryAsync("""
            DROP INDEX UX_TourDestinations_TourSequence ON commerce.TourDestinations;
            CREATE UNIQUE INDEX UX_TourDestinations_TourSequence
                ON commerce.TourDestinations(destination_id, sequence_no);
            """);

        Func<Task> action = () => ApplyMigrationAsync(database);

        await action.Should().ThrowAsync<SqlException>()
            .WithMessage("*TourDestinations sequence index shape mismatch*");
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task Migration_WhenLegacyDraftColumnContainsData_PreservesItWithoutGuessingRegions()
    {
        await using var database = await CreateOldSchemaDatabaseAsync();
        await database.ExecuteNonQueryAsync(
            "ALTER TABLE commerce.Tours ADD destination NVARCHAR(300) COLLATE Vietnamese_100_CI_AS NULL;");
        await database.ExecuteNonQueryAsync("""
            INSERT INTO dbo.OperatorProfiles (user_id) VALUES (52);
            INSERT INTO commerce.Tours (operator_user_id, title, destination, base_price)
            VALUES (52, N'Legacy tour', N'Hội An', 100000);
            """);

        await ApplyMigrationAsync(database);
        await database.ExecuteNonQueryAsync("""
            IF NOT EXISTS (SELECT 1 FROM commerce.Tours WHERE destination = N'Hội An')
                THROW 51000, 'Legacy destination was lost.', 1;
            IF EXISTS (SELECT 1 FROM commerce.TourDestinations)
                THROW 51000, 'Legacy destination was guessed into normalized regions.', 1;
            """);
    }

    private static async Task<SqlServerTestDatabase> CreateOldSchemaDatabaseAsync()
    {
        var database = await SqlServerTestDatabase.CreateEmptyAsync();
        var baselinePath = Path.Combine(
            AppContext.BaseDirectory,
            "Database",
            "Fixtures",
            "tm70_pre_migration_schema.sql");
        await database.ExecuteScriptAsync(baselinePath);
        return database;
    }

    private static Task ApplyMigrationAsync(SqlServerTestDatabase database)
    {
        var migrationPath = Path.Combine(
            AppContext.BaseDirectory,
            "Database",
            "migrations",
            MigrationFileName);
        return database.ExecuteScriptAsync(migrationPath);
    }

    private static Task AssertTargetShapeAsync(SqlServerTestDatabase database)
    {
        return database.ExecuteNonQueryAsync("""
            IF NOT EXISTS (
                SELECT 1 FROM sys.columns AS c
                WHERE c.object_id = OBJECT_ID(N'catalog.Destinations')
                  AND c.name = N'name' AND c.max_length = 600
                  AND c.collation_name = N'Vietnamese_100_CI_AS'
                  AND c.is_nullable = 0)
                THROW 51000, 'Destinations does not have the approved shape.', 1;

            IF NOT EXISTS (
                SELECT 1 FROM sys.key_constraints
                WHERE parent_object_id = OBJECT_ID(N'commerce.TourDestinations')
                  AND name = N'PK_TourDestinations')
                THROW 51000, 'TourDestinations composite key is missing.', 1;

            IF NOT EXISTS (
                SELECT 1 FROM sys.indexes
                WHERE object_id = OBJECT_ID(N'commerce.TourDestinations')
                  AND name = N'UX_TourDestinations_TourSequence'
                  AND is_unique = 1 AND is_disabled = 0)
                THROW 51000, 'TourDestinations sequence uniqueness is missing.', 1;

            IF NOT EXISTS (
                SELECT 1 FROM sys.check_constraints
                WHERE parent_object_id = OBJECT_ID(N'commerce.TourDestinations')
                  AND name = N'CK_TourDestinations_SequencePositive'
                  AND is_disabled = 0 AND is_not_trusted = 0)
                THROW 51000, 'TourDestinations positive sequence constraint is missing.', 1;

            IF NOT EXISTS (
                SELECT 1
                FROM sys.check_constraints
                WHERE parent_object_id = OBJECT_ID(N'commerce.Tours')
                  AND name = N'CK_Tours_BasePriceWholeVnd'
                  AND is_disabled = 0
                  AND is_not_trusted = 0)
                THROW 51000, 'Whole-VND constraint is missing, disabled, or untrusted.', 1;

            DECLARE @definition NVARCHAR(MAX) = LOWER(
                OBJECT_DEFINITION(OBJECT_ID(N'commerce.CK_Tours_BasePriceWholeVnd', N'C')));
            DECLARE @normalizedDefinition NVARCHAR(MAX) = REPLACE(
                REPLACE(
                    REPLACE(
                        REPLACE(
                            REPLACE(@definition, N'[', N''),
                            N']', N''),
                        N'(', N''),
                    N')', N''),
                N' ', N'');

            IF @normalizedDefinition <> N'base_price=floorbase_price'
                THROW 51000, 'Whole-VND constraint has the wrong definition.', 1;
            """);
    }
}