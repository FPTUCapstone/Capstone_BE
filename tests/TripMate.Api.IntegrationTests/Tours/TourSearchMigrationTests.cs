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
    public async Task Migration_FromRecordedBaseline_MatchesFreshSchemaInventoryForTouchedObjects()
    {
        await using var freshDatabase = await SqlServerTestDatabase.CreateAsync();
        await using var upgradedDatabase = await CreateOldSchemaDatabaseAsync();

        await ApplyMigrationAsync(upgradedDatabase);

        var freshInventory = await ReadTm70SchemaInventoryAsync(freshDatabase);
        var upgradedInventory = await ReadTm70SchemaInventoryAsync(upgradedDatabase);

        upgradedInventory.Should().Equal(
            freshInventory,
            "a baseline database upgraded by TM-70 must match the fresh schema for every affected table, column, key, foreign key, check constraint, and index");
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
    public async Task Migration_WhenLegacyDestinationColumnHasWrongShape_RejectsWithoutPartialChanges()
    {
        await using var database = await CreateOldSchemaDatabaseAsync();
        await database.ExecuteNonQueryAsync(
            "ALTER TABLE commerce.Tours ADD destination NVARCHAR(100) NULL;");

        Func<Task> action = () => ApplyMigrationAsync(database);

        await action.Should()
            .ThrowAsync<SqlException>()
            .WithMessage("*legacy destination column shape mismatch*");
        await database.ExecuteNonQueryAsync("""
            IF OBJECT_ID(N'catalog.Destinations', N'U') IS NOT NULL
                THROW 51000, 'Wrong legacy destination shape left Destinations behind.', 1;
            IF OBJECT_ID(N'commerce.TourDestinations', N'U') IS NOT NULL
                THROW 51000, 'Wrong legacy destination shape left TourDestinations behind.', 1;
            IF OBJECT_ID(N'commerce.CK_Tours_BasePriceWholeVnd', N'C') IS NOT NULL
                THROW 51000, 'Wrong legacy destination shape left the price constraint behind.', 1;
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

    private static async Task<IReadOnlyList<string>> ReadTm70SchemaInventoryAsync(
        SqlServerTestDatabase database)
    {
        const string commandText = """
            WITH TargetTables AS (
                SELECT OBJECT_ID(N'commerce.Tours') AS object_id
                UNION ALL SELECT OBJECT_ID(N'catalog.Destinations')
                UNION ALL SELECT OBJECT_ID(N'commerce.TourDestinations')
            ), Inventory AS (
                SELECT CONCAT(N'TABLE|', SCHEMA_NAME(o.schema_id), N'.', o.name) AS item
                FROM sys.objects AS o
                JOIN TargetTables AS target ON target.object_id = o.object_id
                WHERE o.type = N'U'

                UNION ALL

                SELECT CONCAT(
                    N'COLUMN|', SCHEMA_NAME(o.schema_id), N'.', o.name, N'|',
                    c.name, N'|', t.name, N'|', c.max_length,
                    N'|', c.precision, N'|', c.scale, N'|', c.is_nullable,
                    N'|', COALESCE(c.collation_name, N'<NULL>'), N'|', c.is_identity,
                    N'|', c.is_computed, N'|', COALESCE(dc.definition, N'<NULL>'))
                FROM sys.columns AS c
                JOIN sys.objects AS o ON o.object_id = c.object_id
                JOIN TargetTables AS target ON target.object_id = c.object_id
                JOIN sys.types AS t ON t.user_type_id = c.user_type_id
                LEFT JOIN sys.default_constraints AS dc
                    ON dc.parent_object_id = c.object_id
                    AND dc.parent_column_id = c.column_id

                UNION ALL

                SELECT CONCAT(
                    N'INDEX|', SCHEMA_NAME(o.schema_id), N'.', o.name, N'|',
                    i.type, N'|', i.is_unique, N'|', i.is_primary_key,
                    N'|', i.is_unique_constraint, N'|', i.is_disabled,
                    N'|', i.has_filter, N'|', COALESCE(i.filter_definition, N'<NULL>'),
                    N'|', STUFF((
                        SELECT N',' + COL_NAME(ic.object_id, ic.column_id)
                            + CASE WHEN ic.is_descending_key = 1 THEN N':DESC' ELSE N':ASC' END
                        FROM sys.index_columns AS ic
                        WHERE ic.object_id = i.object_id
                            AND ic.index_id = i.index_id
                            AND ic.key_ordinal > 0
                        ORDER BY ic.key_ordinal
                        FOR XML PATH(N''), TYPE).value(N'.', N'nvarchar(max)'), 1, 1, N''),
                    N'|', STUFF((
                        SELECT N',' + COL_NAME(ic.object_id, ic.column_id)
                        FROM sys.index_columns AS ic
                        WHERE ic.object_id = i.object_id
                            AND ic.index_id = i.index_id
                            AND ic.is_included_column = 1
                        ORDER BY ic.index_column_id
                        FOR XML PATH(N''), TYPE).value(N'.', N'nvarchar(max)'), 1, 1, N''))
                FROM sys.indexes AS i
                JOIN sys.objects AS o ON o.object_id = i.object_id
                JOIN TargetTables AS target ON target.object_id = i.object_id
                WHERE i.index_id > 0 AND i.is_hypothetical = 0

                UNION ALL

                SELECT CONCAT(
                    N'FOREIGN_KEY|', SCHEMA_NAME(parent_object.schema_id), N'.', parent_object.name,
                    N'|', SCHEMA_NAME(referenced_object.schema_id), N'.', referenced_object.name,
                    N'|', fk.delete_referential_action, N'|', fk.update_referential_action,
                    N'|', fk.is_disabled, N'|', fk.is_not_trusted, N'|', STUFF((
                        SELECT N',' + COL_NAME(fkc.parent_object_id, fkc.parent_column_id)
                            + N'->' + COL_NAME(fkc.referenced_object_id, fkc.referenced_column_id)
                        FROM sys.foreign_key_columns AS fkc
                        WHERE fkc.constraint_object_id = fk.object_id
                        ORDER BY fkc.constraint_column_id
                        FOR XML PATH(N''), TYPE).value(N'.', N'nvarchar(max)'), 1, 1, N''))
                FROM sys.foreign_keys AS fk
                JOIN sys.objects AS parent_object ON parent_object.object_id = fk.parent_object_id
                JOIN sys.objects AS referenced_object ON referenced_object.object_id = fk.referenced_object_id
                JOIN TargetTables AS target ON target.object_id = fk.parent_object_id

                UNION ALL

                SELECT CONCAT(
                    N'CHECK|', SCHEMA_NAME(o.schema_id), N'.', o.name,
                    N'|', cc.is_disabled, N'|', cc.is_not_trusted, N'|',
                    LOWER(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(
                        OBJECT_DEFINITION(cc.object_id), N'[', N''), N']', N''),
                        N'(', N''), N')', N''), N' ', N''), CHAR(13), N''), CHAR(10), N'')))
                FROM sys.check_constraints AS cc
                JOIN sys.objects AS o ON o.object_id = cc.parent_object_id
                JOIN TargetTables AS target ON target.object_id = cc.parent_object_id
            )
            SELECT item
            FROM Inventory
            ORDER BY item;
            """;

        await using var connection = new SqlConnection(database.ConnectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand(commandText, connection);
        await using var reader = await command.ExecuteReaderAsync();

        var inventory = new List<string>();
        while (await reader.ReadAsync())
        {
            inventory.Add(reader.GetString(0));
        }

        return inventory;
    }

    private static Task AssertTargetShapeAsync(SqlServerTestDatabase database)
    {
        return database.ExecuteNonQueryAsync("""
            IF NOT EXISTS (
                SELECT 1
                FROM sys.columns AS c
                JOIN sys.types AS t ON t.user_type_id = c.user_type_id
                WHERE c.object_id = OBJECT_ID(N'commerce.Tours')
                  AND c.name = N'destination'
                  AND t.name = N'nvarchar'
                  AND c.max_length = 600
                  AND c.collation_name = N'Vietnamese_100_CI_AS'
                  AND c.is_nullable = 1
                  AND c.is_identity = 0
                  AND c.is_computed = 0)
                THROW 51000, 'Tours legacy destination does not have the approved shape.', 1;

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