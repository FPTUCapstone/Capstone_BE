using FluentAssertions;

using Microsoft.Data.SqlClient;

using TripMate.Api.IntegrationTests.Infrastructure;

namespace TripMate.Api.IntegrationTests.Tours;

public sealed class TourCategoryMigrationTests
{
    private const string MigrationFileName = "20261009_add_tour_categories.sql";
    private const string Tm206MigrationFileName = "20260923_add_tour_media.sql";
    private const string Tm207MigrationFileName = "20260927_add_tour_media_management.sql";

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task FreshSchema_HasTransitionalTourCategoryInventory()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();

        await AssertTargetShapeAsync(database);
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task Migration_FromRecordedBaseline_PreservesTourAndMediaWithoutFabricatingTaxonomy()
    {
        await using var freshDatabase = await SqlServerTestDatabase.CreateAsync();
        await using var database = await CreatePreCategoryDatabaseAsync();
        var legacyTourCount = await ReadIntAsync(
            database,
            "SELECT COUNT(*) FROM commerce.Tours WHERE tour_id = 5001;");
        var legacyMediaCount = await ReadIntAsync(
            database,
            "SELECT COUNT(*) FROM commerce.TourMedia WHERE tour_id = 5001;");

        await ApplyMigrationAsync(database);

        await AssertTargetShapeAsync(database);
        (await ReadInventoryAsync(database)).Should().Be(
            await ReadInventoryAsync(freshDatabase),
            "fresh installation and baseline upgrade must converge for every touched schema object");
        (await ReadIntAsync(database, "SELECT COUNT(*) FROM catalog.TourCategories;"))
            .Should().Be(0, "production taxonomy seeding remains a Product/BA gate");
        (await ReadIntAsync(
            database,
            "SELECT COUNT(*) FROM commerce.Tours WHERE tour_id = 5001 AND category_id IS NULL;"))
            .Should().Be(legacyTourCount, "the transitional migration must not guess a category");
        (await ReadIntAsync(
            database,
            "SELECT COUNT(*) FROM commerce.TourMedia WHERE tour_id = 5001;"))
            .Should().Be(legacyMediaCount, "unrelated Tour media must be preserved");
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task Migration_FromRecordedBaseline_IsIdempotent()
    {
        await using var database = await CreatePreCategoryDatabaseAsync();

        await ApplyMigrationAsync(database);
        await database.ExecuteNonQueryAsync("""
            INSERT INTO catalog.TourCategories (code, name, is_active)
            VALUES ('heritage', N'Heritage', 1);

            UPDATE commerce.Tours
            SET category_id = (
                SELECT category_id FROM catalog.TourCategories WHERE code = 'heritage')
            WHERE tour_id = 5001;
            """);

        await ApplyMigrationAsync(database);

        await AssertTargetShapeAsync(database);
        (await ReadIntAsync(database, "SELECT COUNT(*) FROM catalog.TourCategories;"))
            .Should().Be(1);
        (await ReadIntAsync(database, "SELECT COUNT(*) FROM commerce.Tours WHERE category_id IS NOT NULL;"))
            .Should().Be(1);
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task Migration_WhenCategoryTableHasWrongShape_RollsBackWithoutAddingTourColumn()
    {
        await using var database = await CreatePreCategoryDatabaseAsync();
        await database.ExecuteNonQueryAsync("""
            CREATE TABLE catalog.TourCategories (
                category_id BIGINT NOT NULL CONSTRAINT PK_TourCategories PRIMARY KEY,
                code NVARCHAR(20) NULL
            );
            """);

        Func<Task> action = () => ApplyMigrationAsync(database);

        await action.Should().ThrowAsync<SqlException>();
        (await ReadIntAsync(database, """
            SELECT COUNT(*)
            FROM sys.columns
            WHERE object_id = OBJECT_ID(N'commerce.Tours')
              AND name = N'category_id';
            """)).Should().Be(0, "the transaction must roll back every partial change");
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task Migration_WhenActiveNameIndexHasCorrectNameButWrongKeys_RejectsIt()
    {
        await using var database = await CreatePreCategoryDatabaseAsync();
        await CreateCanonicalCategoryTableAsync(database);
        await database.ExecuteNonQueryAsync("""
            CREATE INDEX IX_TourCategories_ActiveName
                ON catalog.TourCategories(name, category_id);
            """);

        Func<Task> action = () => ApplyMigrationAsync(database);

        await action.Should().ThrowAsync<SqlException>()
            .WithMessage("*active-name index keys mismatch*");
        (await ReadIntAsync(database, """
            SELECT COUNT(*)
            FROM sys.columns
            WHERE object_id = OBJECT_ID(N'commerce.Tours')
              AND name = N'category_id';
            """)).Should().Be(0, "index-shape rejection must roll back later migration work");
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task Migration_WhenTourCategoryIndexHasCorrectNameButWrongKeys_RejectsIt()
    {
        await using var database = await CreatePreCategoryDatabaseAsync();
        await CreateCanonicalCategoryTableAsync(database);
        await database.ExecuteNonQueryAsync("""
            CREATE INDEX IX_TourCategories_ActiveName
                ON catalog.TourCategories(is_active, name, category_id);

            ALTER TABLE commerce.Tours ADD category_id INT NULL;
            CREATE INDEX IX_Tours_Category
                ON commerce.Tours(status, category_id);
            """);

        Func<Task> action = () => ApplyMigrationAsync(database);

        await action.Should().ThrowAsync<SqlException>()
            .WithMessage("*Tours category index keys mismatch*");
        (await ReadIntAsync(database, """
            SELECT COUNT(*) FROM sys.foreign_keys
            WHERE parent_object_id = OBJECT_ID(N'commerce.Tours')
              AND name = N'FK_Tours_TourCategories';
            """)).Should().Be(0, "the foreign key created before index validation must roll back");
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task Migration_WhenCodeIndexIsDisabled_RejectsItBeforeTourChanges()
    {
        await using var database = await CreatePreCategoryDatabaseAsync();
        await CreateCanonicalCategoryTableAsync(database);
        await database.ExecuteNonQueryAsync(
            "ALTER INDEX UX_TourCategories_Code ON catalog.TourCategories DISABLE;");

        Func<Task> action = () => ApplyMigrationAsync(database);

        await action.Should().ThrowAsync<SqlException>()
            .WithMessage("*code index shape mismatch*");
        (await ReadIntAsync(database, """
            SELECT COUNT(*) FROM sys.columns
            WHERE object_id = OBJECT_ID(N'commerce.Tours') AND name = N'category_id';
            """)).Should().Be(0, "early rejection must leave the transitional Tour shape untouched");
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task Migration_WhenCodeIndexIsDescending_RejectsIt()
    {
        await using var database = await CreatePreCategoryDatabaseAsync();
        await CreateCanonicalCategoryTableAsync(database);
        await database.ExecuteNonQueryAsync("""
            DROP INDEX UX_TourCategories_Code ON catalog.TourCategories;
            CREATE UNIQUE INDEX UX_TourCategories_Code
                ON catalog.TourCategories(code DESC);
            """);

        Func<Task> action = () => ApplyMigrationAsync(database);

        await action.Should().ThrowAsync<SqlException>()
            .WithMessage("*code index keys mismatch*");
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task Migration_WhenCodeIndexIsClustered_RejectsIt()
    {
        await using var database = await CreatePreCategoryDatabaseAsync();
        await CreateCanonicalCategoryTableAsync(database);
        await database.ExecuteNonQueryAsync("""
            DROP INDEX UX_TourCategories_Code ON catalog.TourCategories;
            ALTER TABLE catalog.TourCategories DROP CONSTRAINT PK_TourCategories;
            ALTER TABLE catalog.TourCategories
                ADD CONSTRAINT PK_TourCategories PRIMARY KEY NONCLUSTERED (category_id);
            CREATE UNIQUE CLUSTERED INDEX UX_TourCategories_Code
                ON catalog.TourCategories(code);
            """);

        Func<Task> action = () => ApplyMigrationAsync(database);

        await action.Should().ThrowAsync<SqlException>()
            .WithMessage("*code index shape mismatch*");
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task Migration_WhenRequiredChecksUseExtraParentheses_AcceptsEquivalentFormatting()
    {
        await using var database = await CreatePreCategoryDatabaseAsync();
        await CreateCanonicalCategoryTableAsync(database);
        await database.ExecuteNonQueryAsync("""
            ALTER TABLE catalog.TourCategories DROP CONSTRAINT CK_TourCategories_CodeNotBlank;
            ALTER TABLE catalog.TourCategories
                ADD CONSTRAINT CK_TourCategories_CodeNotBlank
                CHECK (((LEN((LTRIM((RTRIM((code))))))) > ((0))));
            """);

        await ApplyMigrationAsync(database);

        await AssertTargetShapeAsync(database);
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task Migration_WhenPrimaryKeyHasCorrectNameButWrongColumn_RejectsIt()
    {
        await using var database = await CreatePreCategoryDatabaseAsync();
        await CreateCanonicalCategoryTableAsync(database);
        await database.ExecuteNonQueryAsync("""
            ALTER TABLE catalog.TourCategories DROP CONSTRAINT PK_TourCategories;
            ALTER TABLE catalog.TourCategories
                ADD CONSTRAINT PK_TourCategories PRIMARY KEY (code);
            """);

        Func<Task> action = () => ApplyMigrationAsync(database);

        await action.Should().ThrowAsync<SqlException>()
            .WithMessage("*primary key shape mismatch*");
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task Migration_WhenRequiredConstraintsHaveWrongDefinitions_RejectsThem()
    {
        await using var database = await CreatePreCategoryDatabaseAsync();
        await CreateCanonicalCategoryTableAsync(database);
        await database.ExecuteNonQueryAsync("""
            ALTER TABLE catalog.TourCategories DROP CONSTRAINT CK_TourCategories_CodeNotBlank;
            ALTER TABLE catalog.TourCategories
                ADD CONSTRAINT CK_TourCategories_CodeNotBlank CHECK (LEN(code) >= 0);
            """);

        Func<Task> action = () => ApplyMigrationAsync(database);

        await action.Should().ThrowAsync<SqlException>()
            .WithMessage("*required-value constraint shape mismatch*");
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task Migration_WhenActiveDefaultHasWrongValue_RejectsIt()
    {
        await using var database = await CreatePreCategoryDatabaseAsync();
        await CreateCanonicalCategoryTableAsync(database);
        await database.ExecuteNonQueryAsync("""
            ALTER TABLE catalog.TourCategories DROP CONSTRAINT DF_TourCategories_IsActive;
            ALTER TABLE catalog.TourCategories
                ADD CONSTRAINT DF_TourCategories_IsActive DEFAULT 0 FOR is_active;
            """);

        Func<Task> action = () => ApplyMigrationAsync(database);

        await action.Should().ThrowAsync<SqlException>()
            .WithMessage("*default shape mismatch*");
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task Migration_WhenForeignKeyHasCorrectNameButWrongParentColumn_RejectsIt()
    {
        await using var database = await CreatePreCategoryDatabaseAsync();
        await CreateCanonicalCategoryTableAsync(database);
        await database.ExecuteNonQueryAsync("""
            ALTER TABLE commerce.Tours ADD category_id INT NULL, alternate_category_id INT NULL;
            ALTER TABLE commerce.Tours WITH CHECK
                ADD CONSTRAINT FK_Tours_TourCategories
                FOREIGN KEY (alternate_category_id)
                REFERENCES catalog.TourCategories(category_id);
            """);

        Func<Task> action = () => ApplyMigrationAsync(database);

        await action.Should().ThrowAsync<SqlException>()
            .WithMessage("*foreign key shape mismatch*");
        (await ReadIntAsync(database, """
            SELECT COUNT(*) FROM sys.indexes
            WHERE object_id = OBJECT_ID(N'commerce.Tours') AND name = N'IX_Tours_Category';
            """)).Should().Be(0, "foreign-key rejection must roll back later index creation");
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task Migration_WhenForeignKeyIsUntrusted_RejectsIt()
    {
        await using var database = await CreatePreCategoryDatabaseAsync();
        await CreateCanonicalCategoryTableAsync(database);
        await database.ExecuteNonQueryAsync("""
            ALTER TABLE commerce.Tours ADD category_id INT NULL;
            ALTER TABLE commerce.Tours WITH NOCHECK
                ADD CONSTRAINT FK_Tours_TourCategories
                FOREIGN KEY (category_id)
                REFERENCES catalog.TourCategories(category_id);
            """);

        Func<Task> action = () => ApplyMigrationAsync(database);

        await action.Should().ThrowAsync<SqlException>()
            .WithMessage("*foreign key shape mismatch*");
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task Constraints_EnforceStableUniqueCodeRequiredEnglishNameAndForeignKey()
    {
        await using var database = await CreatePreCategoryDatabaseAsync();
        await ApplyMigrationAsync(database);

        await database.ExecuteNonQueryAsync("""
            INSERT INTO catalog.TourCategories (code, name, is_active)
            VALUES ('heritage', N'Heritage', 1),
                   ('nature', N'Nature', 0);

            UPDATE commerce.Tours
            SET category_id = (
                SELECT category_id FROM catalog.TourCategories WHERE code = 'heritage')
            WHERE tour_id = 5001;
            """);

        Func<Task> duplicateCode = () => database.ExecuteNonQueryAsync("""
            INSERT INTO catalog.TourCategories (code, name)
            VALUES ('HERITAGE', N'Another Heritage');
            """);
        (await duplicateCode.Should().ThrowAsync<SqlException>()).Which.Number
            .Should().BeOneOf(2601, 2627);

        Func<Task> blankName = () => database.ExecuteNonQueryAsync("""
            INSERT INTO catalog.TourCategories (code, name)
            VALUES ('blank-name', N'   ');
            """);
        (await blankName.Should().ThrowAsync<SqlException>()).Which.Number.Should().Be(547);

        Func<Task> invalidForeignKey = () => database.ExecuteNonQueryAsync(
            "UPDATE commerce.Tours SET category_id = 2147483647 WHERE tour_id = 5001;");
        (await invalidForeignKey.Should().ThrowAsync<SqlException>()).Which.Number.Should().Be(547);

        (await ReadIntAsync(
            database,
            "SELECT COUNT(*) FROM catalog.TourCategories WHERE code = 'nature' AND is_active = 0;"))
            .Should().Be(1, "inactive taxonomy entries remain representable");
    }

    private static async Task<SqlServerTestDatabase> CreatePreCategoryDatabaseAsync()
    {
        var database = await SqlServerTestDatabase.CreateEmptyAsync();
        var fixtureRoot = Path.Combine(AppContext.BaseDirectory, "Database", "Fixtures");
        var migrationRoot = Path.Combine(AppContext.BaseDirectory, "Database", "migrations");
        await database.ExecuteScriptAsync(Path.Combine(fixtureRoot, "tm206_pre_migration_schema.sql"));
        await database.ExecuteScriptAsync(Path.Combine(migrationRoot, Tm206MigrationFileName));
        await database.ExecuteScriptAsync(Path.Combine(migrationRoot, Tm207MigrationFileName));
        await database.ExecuteNonQueryAsync("""
            INSERT INTO commerce.TourMedia
                (tour_id, cloudinary_public_id, delivery_url, alt_text, sort_order, is_primary)
            VALUES
                (5001, N'tripmate/tours/5001/category-baseline',
                 N'https://res.cloudinary.com/demo/category-baseline.jpg',
                 N'Legacy tour image', 1, 1);
            """);
        return database;
    }

    private static Task CreateCanonicalCategoryTableAsync(SqlServerTestDatabase database) =>
        database.ExecuteNonQueryAsync("""
            CREATE TABLE catalog.TourCategories (
                category_id INT IDENTITY(1,1) NOT NULL
                    CONSTRAINT PK_TourCategories PRIMARY KEY,
                code VARCHAR(50) COLLATE Latin1_General_100_CI_AS NOT NULL,
                name NVARCHAR(100) COLLATE Latin1_General_100_CI_AS NOT NULL,
                is_active BIT NOT NULL
                    CONSTRAINT DF_TourCategories_IsActive DEFAULT 1,
                CONSTRAINT CK_TourCategories_CodeNotBlank
                    CHECK (LEN(LTRIM(RTRIM(code))) > 0),
                CONSTRAINT CK_TourCategories_NameNotBlank
                    CHECK (LEN(LTRIM(RTRIM(name))) > 0)
            );
            CREATE UNIQUE INDEX UX_TourCategories_Code
                ON catalog.TourCategories(code);
            """);

    private static Task ApplyMigrationAsync(SqlServerTestDatabase database)
    {
        var migrationPath = Path.Combine(
            AppContext.BaseDirectory,
            "Database",
            "migrations",
            MigrationFileName);
        return database.ExecuteScriptAsync(migrationPath);
    }

    private static Task AssertTargetShapeAsync(SqlServerTestDatabase database) =>
        database.ExecuteNonQueryAsync("""
            IF OBJECT_ID(N'catalog.TourCategories', N'U') IS NULL
                THROW 51000, 'catalog.TourCategories is missing.', 1;

            IF NOT EXISTS (
                SELECT 1
                FROM sys.columns AS c
                JOIN sys.types AS t ON t.user_type_id = c.user_type_id
                WHERE c.object_id = OBJECT_ID(N'catalog.TourCategories')
                  AND c.name = N'category_id'
                  AND t.name = N'int'
                  AND c.is_identity = 1
                  AND c.is_nullable = 0)
                THROW 51000, 'TourCategories.category_id shape mismatch.', 1;

            IF NOT EXISTS (
                SELECT 1
                FROM sys.columns AS c
                JOIN sys.types AS t ON t.user_type_id = c.user_type_id
                WHERE c.object_id = OBJECT_ID(N'catalog.TourCategories')
                  AND c.name = N'code'
                  AND t.name = N'varchar'
                  AND c.max_length = 50
                  AND c.is_nullable = 0)
                THROW 51000, 'TourCategories.code shape mismatch.', 1;

            IF NOT EXISTS (
                SELECT 1
                FROM sys.columns AS c
                JOIN sys.types AS t ON t.user_type_id = c.user_type_id
                WHERE c.object_id = OBJECT_ID(N'catalog.TourCategories')
                  AND c.name = N'name'
                  AND t.name = N'nvarchar'
                  AND c.max_length = 200
                  AND c.is_nullable = 0)
                THROW 51000, 'TourCategories.name shape mismatch.', 1;

            IF NOT EXISTS (
                SELECT 1
                FROM sys.columns
                WHERE object_id = OBJECT_ID(N'catalog.TourCategories')
                  AND name = N'is_active'
                  AND system_type_id = TYPE_ID(N'bit')
                  AND is_nullable = 0)
                THROW 51000, 'TourCategories.is_active shape mismatch.', 1;

            IF NOT EXISTS (
                SELECT 1
                FROM sys.indexes
                WHERE object_id = OBJECT_ID(N'catalog.TourCategories')
                  AND name = N'UX_TourCategories_Code'
                  AND is_unique = 1)
                THROW 51000, 'TourCategories stable code index is missing.', 1;

            IF NOT EXISTS (
                SELECT 1
                FROM sys.columns AS c
                JOIN sys.types AS t ON t.user_type_id = c.user_type_id
                WHERE c.object_id = OBJECT_ID(N'commerce.Tours')
                  AND c.name = N'category_id'
                  AND t.name = N'int'
                  AND c.is_nullable = 1)
                THROW 51000, 'Tours.category_id transitional shape mismatch.', 1;

            IF NOT EXISTS (
                SELECT 1
                FROM sys.foreign_keys AS fk
                JOIN sys.foreign_key_columns AS fkc
                  ON fkc.constraint_object_id = fk.object_id
                JOIN sys.columns AS parent_column
                  ON parent_column.object_id = fkc.parent_object_id
                 AND parent_column.column_id = fkc.parent_column_id
                JOIN sys.columns AS referenced_column
                  ON referenced_column.object_id = fkc.referenced_object_id
                 AND referenced_column.column_id = fkc.referenced_column_id
                WHERE fk.parent_object_id = OBJECT_ID(N'commerce.Tours')
                  AND fk.referenced_object_id = OBJECT_ID(N'catalog.TourCategories')
                  AND fk.name = N'FK_Tours_TourCategories'
                  AND fk.delete_referential_action = 0
                  AND fk.update_referential_action = 0
                  AND fk.is_disabled = 0
                  AND fk.is_not_trusted = 0
                  AND fkc.constraint_column_id = 1
                  AND parent_column.name = N'category_id'
                  AND referenced_column.name = N'category_id')
                THROW 51000, 'Tours category foreign key is missing or unsafe.', 1;

            IF OBJECT_ID(N'commerce.TourCategories', N'U') IS NOT NULL
                THROW 51000, 'Unexpected TourCategory join table exists.', 1;
            """);

    private static Task<int> ReadIntAsync(SqlServerTestDatabase database, string sql) =>
        database.ExecuteScalarAsync<int>(sql);

    private static Task<string> ReadInventoryAsync(SqlServerTestDatabase database) =>
        database.ExecuteScalarAsync<string>("""
            SELECT (
                SELECT inventory_kind, object_name, item_name, definition
                FROM (
                    SELECT
                        N'COLUMN' AS inventory_kind,
                        CONCAT(OBJECT_SCHEMA_NAME(c.object_id), N'.', OBJECT_NAME(c.object_id)) AS object_name,
                        c.name AS item_name,
                        CONCAT(t.name, N'|', c.max_length, N'|', c.is_nullable, N'|',
                            c.is_identity, N'|', COALESCE(c.collation_name, N'')) AS definition
                    FROM sys.columns AS c
                    JOIN sys.types AS t ON t.user_type_id = c.user_type_id
                    WHERE c.object_id IN (
                        OBJECT_ID(N'catalog.TourCategories'),
                        OBJECT_ID(N'commerce.Tours'))
                      AND (c.object_id = OBJECT_ID(N'catalog.TourCategories')
                           OR c.name = N'category_id')

                    UNION ALL

                    SELECT
                        N'INDEX',
                        CONCAT(OBJECT_SCHEMA_NAME(i.object_id), N'.', OBJECT_NAME(i.object_id)),
                        i.name,
                        CONCAT(i.type, N'|', i.is_unique, N'|', i.has_filter, N'|',
                            i.is_disabled, N'|', i.is_hypothetical, N'|',
                            STRING_AGG(CONCAT(ic.key_ordinal, N':', c.name, N':',
                                ic.is_descending_key), N',')
                                WITHIN GROUP (ORDER BY ic.key_ordinal))
                    FROM sys.indexes AS i
                    JOIN sys.index_columns AS ic
                      ON ic.object_id = i.object_id AND ic.index_id = i.index_id
                    JOIN sys.columns AS c
                      ON c.object_id = ic.object_id AND c.column_id = ic.column_id
                    WHERE i.object_id IN (
                        OBJECT_ID(N'catalog.TourCategories'),
                        OBJECT_ID(N'commerce.Tours'))
                      AND i.name IN (
                        N'PK_TourCategories', N'UX_TourCategories_Code',
                        N'IX_TourCategories_ActiveName', N'IX_Tours_Category')
                      AND ic.is_included_column = 0
                    GROUP BY i.object_id, i.name, i.type, i.is_unique, i.has_filter,
                        i.is_disabled, i.is_hypothetical

                    UNION ALL

                    SELECT
                        N'CONSTRAINT',
                        CONCAT(OBJECT_SCHEMA_NAME(o.parent_object_id), N'.', OBJECT_NAME(o.parent_object_id)),
                        o.name,
                        REPLACE(REPLACE(OBJECT_DEFINITION(o.object_id), CHAR(13), N''), CHAR(10), N'')
                    FROM sys.objects AS o
                    WHERE o.parent_object_id IN (
                        OBJECT_ID(N'catalog.TourCategories'),
                        OBJECT_ID(N'commerce.Tours'))
                      AND o.name IN (
                        N'DF_TourCategories_IsActive', N'CK_TourCategories_CodeNotBlank',
                        N'CK_TourCategories_NameNotBlank', N'FK_Tours_TourCategories')

                    UNION ALL

                    SELECT
                        N'FOREIGN_KEY',
                        CONCAT(OBJECT_SCHEMA_NAME(fk.parent_object_id), N'.',
                            OBJECT_NAME(fk.parent_object_id)),
                        fk.name,
                        CONCAT(parent_column.name, N'->',
                            OBJECT_SCHEMA_NAME(fk.referenced_object_id), N'.',
                            OBJECT_NAME(fk.referenced_object_id), N'.', referenced_column.name,
                            N'|', fk.delete_referential_action, N'|',
                            fk.update_referential_action, N'|', fk.is_disabled, N'|',
                            fk.is_not_trusted)
                    FROM sys.foreign_keys AS fk
                    JOIN sys.foreign_key_columns AS fkc
                      ON fkc.constraint_object_id = fk.object_id
                    JOIN sys.columns AS parent_column
                      ON parent_column.object_id = fkc.parent_object_id
                     AND parent_column.column_id = fkc.parent_column_id
                    JOIN sys.columns AS referenced_column
                      ON referenced_column.object_id = fkc.referenced_object_id
                     AND referenced_column.column_id = fkc.referenced_column_id
                    WHERE fk.parent_object_id = OBJECT_ID(N'commerce.Tours')
                      AND fk.name = N'FK_Tours_TourCategories'
                ) AS inventory
                ORDER BY inventory_kind, object_name, item_name
                FOR JSON PATH
            );
            """);
}