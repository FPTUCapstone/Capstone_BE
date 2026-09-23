using FluentAssertions;

using Microsoft.Data.SqlClient;

using TripMate.Api.IntegrationTests.Infrastructure;

namespace TripMate.Api.IntegrationTests.Tours;

public sealed class TourMediaMigrationTests
{
    private const string MigrationFileName = "20260923_add_tour_media.sql";

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task FreshSchema_HasApprovedTourMediaInventory()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();

        await AssertApprovedTourMediaShapeAsync(database);
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task Migration_FromRecordedBaseline_MatchesFreshInventoryAndPreservesExistingData()
    {
        await using var freshDatabase = await SqlServerTestDatabase.CreateAsync();
        await using var upgradedDatabase = await CreateBaselineDatabaseAsync();
        var before = await ReadLegacyStateAsync(upgradedDatabase);

        await ApplyMigrationAsync(upgradedDatabase);

        await AssertApprovedTourMediaShapeAsync(freshDatabase);
        await AssertApprovedTourMediaShapeAsync(upgradedDatabase);
        var freshInventory = await ReadTourMediaInventoryAsync(freshDatabase);
        var upgradedInventory = await ReadTourMediaInventoryAsync(upgradedDatabase);
        var after = await ReadLegacyStateAsync(upgradedDatabase);

        upgradedInventory.Should().Equal(
            freshInventory,
            "a pre-TM-206 database upgraded by the migration must match the fresh schema for every TourMedia column, default, key, foreign key, check, and index");
        after.Should().Equal(
            before,
            "the additive migration must preserve existing Tour, schedule, POI, POIPhoto, and identity state");
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task Migration_FromRecordedBaseline_IsIdempotent()
    {
        await using var database = await CreateBaselineDatabaseAsync();

        await ApplyMigrationAsync(database);
        var firstInventory = await ReadTourMediaInventoryAsync(database);
        var firstLegacyState = await ReadLegacyStateAsync(database);

        await ApplyMigrationAsync(database);

        var secondInventory = await ReadTourMediaInventoryAsync(database);
        var secondLegacyState = await ReadLegacyStateAsync(database);
        secondInventory.Should().Equal(firstInventory);
        secondLegacyState.Should().Equal(firstLegacyState);
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task Migration_WhenSameNamedTableHasWrongShape_RollsBackWithoutRepairingIt()
    {
        await using var database = await CreateBaselineDatabaseAsync();
        await database.ExecuteNonQueryAsync("""
            CREATE TABLE commerce.TourMedia (
                tour_media_id INT NOT NULL CONSTRAINT PK_TourMedia PRIMARY KEY
            );
            """);
        var before = await ReadLegacyStateAsync(database);

        Func<Task> action = () => ApplyMigrationAsync(database);

        await action.Should().ThrowAsync<SqlException>();
        (await ReadIntAsync(database, """
            SELECT COUNT(*)
            FROM sys.columns
            WHERE object_id = OBJECT_ID(N'commerce.TourMedia');
            """)).Should().Be(1, "the failed migration must not partially repair an incompatible table");
        (await ReadLegacyStateAsync(database)).Should().Equal(before);
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task Migration_WhenActivePrimaryIndexHasWrongKeys_RejectsWithoutReplacingIt()
    {
        await using var database = await CreateBaselineDatabaseAsync();
        await CreateNearCanonicalTourMediaAsync(
            database,
            wrongPrimaryIndex: true,
            wrongLifecycleConstraint: false);
        var before = await ReadLegacyStateAsync(database);

        Func<Task> action = () => ApplyMigrationAsync(database);

        await action.Should().ThrowAsync<SqlException>();
        (await ReadStringAsync(database, """
            SELECT INDEX_COL(N'commerce.TourMedia',
                INDEXPROPERTY(OBJECT_ID(N'commerce.TourMedia'),
                    N'UX_TourMedia_ActivePrimary', N'IndexId'), 2);
            """)).Should().Be("sort_order");
        (await ReadLegacyStateAsync(database)).Should().Equal(before);
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task Migration_WhenLifecycleConstraintHasWrongDefinition_RejectsWithoutReplacingIt()
    {
        await using var database = await CreateBaselineDatabaseAsync();
        await CreateNearCanonicalTourMediaAsync(
            database,
            wrongPrimaryIndex: false,
            wrongLifecycleConstraint: true);
        var before = await ReadLegacyStateAsync(database);

        Func<Task> action = () => ApplyMigrationAsync(database);

        await action.Should().ThrowAsync<SqlException>();
        (await ReadStringAsync(database, """
            SELECT OBJECT_DEFINITION(
                OBJECT_ID(N'commerce.CK_TourMedia_Lifecycle', N'C'));
            """)).Should().Contain("Archived");
        (await ReadLegacyStateAsync(database)).Should().Equal(before);
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task FilteredIndexes_AllowZeroOrOneActivePrimaryAndRejectActiveDuplicates()
    {
        await using var database = await CreateBaselineDatabaseAsync();
        await ApplyMigrationAsync(database);

        await database.ExecuteNonQueryAsync("""
            INSERT INTO commerce.TourMedia
                (tour_id, cloudinary_public_id, delivery_url, sort_order, is_primary)
            VALUES
                (5001, N'tours/5001/non-primary', N'https://res.cloudinary.com/demo/non-primary.jpg', 1, 0);

            INSERT INTO commerce.TourMedia
                (tour_id, cloudinary_public_id, delivery_url, sort_order, is_primary)
            VALUES
                (5001, N'tours/5001/primary', N'https://res.cloudinary.com/demo/primary.jpg', 2, 1);
            """);

        Func<Task> duplicatePrimary = () => database.ExecuteNonQueryAsync("""
            INSERT INTO commerce.TourMedia
                (tour_id, cloudinary_public_id, delivery_url, sort_order, is_primary)
            VALUES
                (5001, N'tours/5001/second-primary', N'https://res.cloudinary.com/demo/second-primary.jpg', 3, 1);
            """);
        var primaryException = await duplicatePrimary.Should().ThrowAsync<SqlException>();
        primaryException.Which.Number.Should().BeOneOf(2601, 2627);

        Func<Task> duplicateOrder = () => database.ExecuteNonQueryAsync("""
            INSERT INTO commerce.TourMedia
                (tour_id, cloudinary_public_id, delivery_url, sort_order, is_primary)
            VALUES
                (5001, N'tours/5001/duplicate-order', N'https://res.cloudinary.com/demo/duplicate-order.jpg', 1, 0);
            """);
        var orderException = await duplicateOrder.Should().ThrowAsync<SqlException>();
        orderException.Which.Number.Should().BeOneOf(2601, 2627);

        await database.ExecuteNonQueryAsync("""
            INSERT INTO commerce.TourMedia
                (tour_id, cloudinary_public_id, delivery_url, sort_order,
                 is_primary, lifecycle_status, deleted_at)
            VALUES
                (5001, N'tours/5001/deleted-a', N'https://res.cloudinary.com/demo/deleted-a.jpg',
                 2, 1, 'Deleted', SYSUTCDATETIME()),
                (5001, N'tours/5001/deleted-b', N'https://res.cloudinary.com/demo/deleted-b.jpg',
                 2, 1, 'Deleted', SYSUTCDATETIME());
            """);

        (await ReadIntAsync(database, """
            SELECT COUNT(*)
            FROM commerce.TourMedia
            WHERE tour_id = 5001
              AND lifecycle_status = 'Active'
              AND is_primary = 1;
            """)).Should().Be(1);
        (await ReadIntAsync(database, """
            SELECT COUNT(*) FROM commerce.TourMedia WHERE tour_id = 5001;
            """)).Should().Be(4);
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task Lifecycle_SoftDeleteKeepsRowAndEnforcesDeletedAtConsistency()
    {
        await using var database = await CreateBaselineDatabaseAsync();
        await ApplyMigrationAsync(database);
        await database.ExecuteNonQueryAsync("""
            INSERT INTO commerce.TourMedia
                (tour_id, cloudinary_public_id, delivery_url, sort_order, is_primary)
            VALUES
                (5001, N'tours/5001/soft-delete', N'https://res.cloudinary.com/demo/soft-delete.jpg', 1, 1);

            UPDATE commerce.TourMedia
            SET lifecycle_status = 'Deleted',
                deleted_at = SYSUTCDATETIME(),
                updated_at = SYSUTCDATETIME()
            WHERE cloudinary_public_id = N'tours/5001/soft-delete';
            """);

        (await ReadIntAsync(database, """
            SELECT COUNT(*)
            FROM commerce.TourMedia
            WHERE cloudinary_public_id = N'tours/5001/soft-delete'
              AND lifecycle_status = 'Deleted'
              AND deleted_at IS NOT NULL;
            """)).Should().Be(1, "normal media removal is a retained soft-deleted row");

        Func<Task> activeWithDeletedAt = () => database.ExecuteNonQueryAsync("""
            INSERT INTO commerce.TourMedia
                (tour_id, cloudinary_public_id, delivery_url, sort_order,
                 is_primary, lifecycle_status, deleted_at)
            VALUES
                (5001, N'tours/5001/invalid-active', N'https://res.cloudinary.com/demo/invalid-active.jpg',
                 2, 0, 'Active', SYSUTCDATETIME());
            """);
        var activeException = await activeWithDeletedAt.Should().ThrowAsync<SqlException>();
        activeException.Which.Number.Should().Be(547);

        Func<Task> deletedWithoutTimestamp = () => database.ExecuteNonQueryAsync("""
            INSERT INTO commerce.TourMedia
                (tour_id, cloudinary_public_id, delivery_url, sort_order,
                 is_primary, lifecycle_status, deleted_at)
            VALUES
                (5001, N'tours/5001/invalid-deleted', N'https://res.cloudinary.com/demo/invalid-deleted.jpg',
                 2, 0, 'Deleted', NULL);
            """);
        var deletedException = await deletedWithoutTimestamp.Should().ThrowAsync<SqlException>();
        deletedException.Which.Number.Should().Be(547);
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task ParentTourPhysicalDelete_CascadesTourMediaAndLeavesPoiPhotoUntouched()
    {
        await using var database = await CreateBaselineDatabaseAsync();
        await ApplyMigrationAsync(database);
        await database.ExecuteNonQueryAsync("""
            INSERT INTO commerce.TourMedia
                (tour_id, cloudinary_public_id, delivery_url, sort_order, is_primary)
            VALUES
                (5001, N'tours/5001/cascade', N'https://res.cloudinary.com/demo/cascade.jpg', 1, 1);

            DELETE FROM commerce.TourSchedules WHERE tour_id = 5001;
            DELETE FROM commerce.Tours WHERE tour_id = 5001;
            """);

        (await ReadIntAsync(database, """
            SELECT COUNT(*) FROM commerce.TourMedia WHERE tour_id = 5001;
            """)).Should().Be(0, "physical deletion of the parent Tour owns the cascade");
        (await ReadIntAsync(database, """
            SELECT COUNT(*)
            FROM catalog.POIPhotos
            WHERE photo_id = 4001
              AND poi_id = 3001
              AND url = N'https://example.invalid/poi-legacy.jpg'
              AND caption = N'Existing POI-owned image';
            """)).Should().Be(1, "TourMedia must remain isolated from POIPhotos");
    }

    private static async Task<SqlServerTestDatabase> CreateBaselineDatabaseAsync()
    {
        var database = await SqlServerTestDatabase.CreateEmptyAsync();
        var fixturePath = Path.Combine(
            AppContext.BaseDirectory,
            "Database",
            "Fixtures",
            "tm206_pre_migration_schema.sql");
        await database.ExecuteScriptAsync(fixturePath);
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

    private static Task CreateNearCanonicalTourMediaAsync(
        SqlServerTestDatabase database,
        bool wrongPrimaryIndex,
        bool wrongLifecycleConstraint)
    {
        const string template = """
            CREATE TABLE commerce.TourMedia (
                tour_media_id BIGINT IDENTITY(1,1) NOT NULL
                    CONSTRAINT PK_TourMedia PRIMARY KEY,
                tour_id BIGINT NOT NULL,
                cloudinary_public_id NVARCHAR(500) NOT NULL,
                delivery_url NVARCHAR(1000) NOT NULL,
                caption NVARCHAR(500) NULL,
                sort_order INT NOT NULL,
                is_primary BIT NOT NULL
                    CONSTRAINT DF_TourMedia_IsPrimary DEFAULT 0,
                lifecycle_status VARCHAR(16) NOT NULL
                    CONSTRAINT DF_TourMedia_LifecycleStatus DEFAULT 'Active',
                created_at DATETIME2 NOT NULL
                    CONSTRAINT DF_TourMedia_CreatedAt DEFAULT SYSUTCDATETIME(),
                updated_at DATETIME2 NOT NULL
                    CONSTRAINT DF_TourMedia_UpdatedAt DEFAULT SYSUTCDATETIME(),
                deleted_at DATETIME2 NULL,
                CONSTRAINT FK_TourMedia_Tours FOREIGN KEY (tour_id)
                    REFERENCES commerce.Tours(tour_id) ON DELETE CASCADE,
                CONSTRAINT CK_TourMedia_SortOrderPositive CHECK (sort_order > 0),
                CONSTRAINT CK_TourMedia_Lifecycle CHECK (__LIFECYCLE_CHECK__),
                CONSTRAINT CK_TourMedia_DeletedAt CHECK (
                    (lifecycle_status = 'Active' AND deleted_at IS NULL)
                    OR (lifecycle_status = 'Deleted' AND deleted_at IS NOT NULL))
            );

            CREATE UNIQUE INDEX UX_TourMedia_ActiveSortOrder
                ON commerce.TourMedia(tour_id, sort_order)
                WHERE lifecycle_status = 'Active';
            CREATE UNIQUE INDEX UX_TourMedia_ActivePrimary
                ON commerce.TourMedia(__PRIMARY_KEYS__)
                WHERE lifecycle_status = 'Active' AND is_primary = 1;
            CREATE INDEX IX_TourMedia_TourLifecycleOrder
                ON commerce.TourMedia(
                    tour_id, lifecycle_status, sort_order, tour_media_id);
            """;

        var lifecycleCheck = wrongLifecycleConstraint
            ? "lifecycle_status IN ('Active','Deleted','Archived')"
            : "lifecycle_status IN ('Active','Deleted')";
        var primaryKeys = wrongPrimaryIndex
            ? "tour_id, sort_order"
            : "tour_id";
        var commandText = template
            .Replace("__LIFECYCLE_CHECK__", lifecycleCheck, StringComparison.Ordinal)
            .Replace("__PRIMARY_KEYS__", primaryKeys, StringComparison.Ordinal);

        return database.ExecuteNonQueryAsync(commandText);
    }

    private static Task AssertApprovedTourMediaShapeAsync(SqlServerTestDatabase database)
    {
        return database.ExecuteNonQueryAsync("""
            IF OBJECT_ID(N'commerce.TourMedia', N'U') IS NULL
                THROW 51000, 'commerce.TourMedia is missing.', 1;

            IF (SELECT COUNT(*) FROM sys.columns
                WHERE object_id = OBJECT_ID(N'commerce.TourMedia')) <> 11
                THROW 51000, 'TourMedia column count mismatch.', 1;

            IF EXISTS (
                SELECT expected.name
                FROM (VALUES
                    (N'tour_media_id', N'bigint', 8, 19, 0, 0, 1, CAST(NULL AS NVARCHAR(128))),
                    (N'tour_id', N'bigint', 8, 19, 0, 0, 0, NULL),
                    (N'cloudinary_public_id', N'nvarchar', 1000, 0, 0, 0, 0, NULL),
                    (N'delivery_url', N'nvarchar', 2000, 0, 0, 0, 0, NULL),
                    (N'caption', N'nvarchar', 1000, 0, 0, 1, 0, NULL),
                    (N'sort_order', N'int', 4, 10, 0, 0, 0, NULL),
                    (N'is_primary', N'bit', 1, 1, 0, 0, 0, N'0'),
                    (N'lifecycle_status', N'varchar', 16, 0, 0, 0, 0, N'''active'''),
                    (N'created_at', N'datetime2', 8, 27, 7, 0, 0, N'sysutcdatetime'),
                    (N'updated_at', N'datetime2', 8, 27, 7, 0, 0, N'sysutcdatetime'),
                    (N'deleted_at', N'datetime2', 8, 27, 7, 1, 0, NULL)
                ) AS expected(
                    name, type_name, max_length, precision_value, scale_value,
                    is_nullable, is_identity, default_definition)
                LEFT JOIN sys.columns AS actual
                    ON actual.object_id = OBJECT_ID(N'commerce.TourMedia')
                    AND actual.name = expected.name
                LEFT JOIN sys.types AS actual_type
                    ON actual_type.user_type_id = actual.user_type_id
                LEFT JOIN sys.default_constraints AS default_constraint
                    ON default_constraint.parent_object_id = actual.object_id
                    AND default_constraint.parent_column_id = actual.column_id
                WHERE actual.column_id IS NULL
                   OR actual_type.name <> expected.type_name
                   OR actual.max_length <> expected.max_length
                   OR (expected.precision_value > 0
                       AND actual.precision <> expected.precision_value)
                   OR actual.scale <> expected.scale_value
                   OR actual.is_nullable <> expected.is_nullable
                   OR actual.is_identity <> expected.is_identity
                   OR ISNULL(LOWER(REPLACE(REPLACE(REPLACE(
                        default_constraint.definition, N'(', N''), N')', N''), N' ', N'')), N'<null>')
                      <> ISNULL(expected.default_definition, N'<null>'))
                THROW 51000, 'TourMedia column/default shape mismatch.', 1;

            IF NOT EXISTS (
                SELECT 1 FROM sys.key_constraints
                WHERE parent_object_id = OBJECT_ID(N'commerce.TourMedia')
                  AND name = N'PK_TourMedia' AND type = N'PK')
                THROW 51000, 'TourMedia primary key mismatch.', 1;

            IF NOT EXISTS (
                SELECT 1 FROM sys.foreign_keys
                WHERE parent_object_id = OBJECT_ID(N'commerce.TourMedia')
                  AND referenced_object_id = OBJECT_ID(N'commerce.Tours')
                  AND name = N'FK_TourMedia_Tours'
                  AND delete_referential_action = 1
                  AND update_referential_action = 0
                  AND is_disabled = 0 AND is_not_trusted = 0)
                THROW 51000, 'TourMedia Tour cascade foreign key mismatch.', 1;

            DECLARE @sortCheck NVARCHAR(MAX) = LOWER(REPLACE(REPLACE(REPLACE(REPLACE(
                OBJECT_DEFINITION(OBJECT_ID(N'commerce.CK_TourMedia_SortOrderPositive', N'C')),
                N'[', N''), N']', N''), N'(', N''), N')', N''));
            SET @sortCheck = REPLACE(@sortCheck, N' ', N'');
            IF @sortCheck <> N'sort_order>0'
                THROW 51000, 'TourMedia sort-order check mismatch.', 1;

            DECLARE @lifecycleCheck NVARCHAR(MAX) = LOWER(REPLACE(REPLACE(REPLACE(REPLACE(
                OBJECT_DEFINITION(OBJECT_ID(N'commerce.CK_TourMedia_Lifecycle', N'C')),
                N'[', N''), N']', N''), N'(', N''), N')', N''));
            SET @lifecycleCheck = REPLACE(@lifecycleCheck, N' ', N'');
            IF @lifecycleCheck NOT IN (
                N'lifecycle_status=''active''orlifecycle_status=''deleted''',
                N'lifecycle_status=''deleted''orlifecycle_status=''active''')
                THROW 51000, 'TourMedia lifecycle check mismatch.', 1;

            DECLARE @deletedAtCheck NVARCHAR(MAX) = LOWER(REPLACE(REPLACE(REPLACE(REPLACE(
                OBJECT_DEFINITION(OBJECT_ID(N'commerce.CK_TourMedia_DeletedAt', N'C')),
                N'[', N''), N']', N''), N'(', N''), N')', N''));
            SET @deletedAtCheck = REPLACE(@deletedAtCheck, N' ', N'');
            IF @deletedAtCheck <> N'lifecycle_status=''active''anddeleted_atisnullorlifecycle_status=''deleted''anddeleted_atisnotnull'
                THROW 51000, 'TourMedia deleted-at consistency check mismatch.', 1;

            DECLARE @activeOrderIndexId INT = INDEXPROPERTY(
                OBJECT_ID(N'commerce.TourMedia'),
                N'UX_TourMedia_ActiveSortOrder', N'IndexId');
            IF @activeOrderIndexId IS NULL
               OR INDEXPROPERTY(OBJECT_ID(N'commerce.TourMedia'),
                    N'UX_TourMedia_ActiveSortOrder', N'IsUnique') <> 1
               OR INDEX_COL(N'commerce.TourMedia', @activeOrderIndexId, 1) <> N'tour_id'
               OR INDEX_COL(N'commerce.TourMedia', @activeOrderIndexId, 2) <> N'sort_order'
               OR INDEX_COL(N'commerce.TourMedia', @activeOrderIndexId, 3) IS NOT NULL
               OR LOWER(REPLACE(REPLACE(REPLACE(REPLACE(
                    (SELECT filter_definition FROM sys.indexes
                     WHERE object_id = OBJECT_ID(N'commerce.TourMedia')
                       AND index_id = @activeOrderIndexId),
                    N'[', N''), N']', N''), N'(', N''), N')', N''))
                    <> N'lifecycle_status=''active'''
                THROW 51000, 'TourMedia active sort-order index mismatch.', 1;

            DECLARE @primaryIndexId INT = INDEXPROPERTY(
                OBJECT_ID(N'commerce.TourMedia'),
                N'UX_TourMedia_ActivePrimary', N'IndexId');
            IF @primaryIndexId IS NULL
               OR INDEXPROPERTY(OBJECT_ID(N'commerce.TourMedia'),
                    N'UX_TourMedia_ActivePrimary', N'IsUnique') <> 1
               OR INDEX_COL(N'commerce.TourMedia', @primaryIndexId, 1) <> N'tour_id'
               OR INDEX_COL(N'commerce.TourMedia', @primaryIndexId, 2) IS NOT NULL
               OR LOWER(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(
                    (SELECT filter_definition FROM sys.indexes
                     WHERE object_id = OBJECT_ID(N'commerce.TourMedia')
                       AND index_id = @primaryIndexId),
                    N'[', N''), N']', N''), N'(', N''), N')', N''), N' ', N''))
                    <> N'lifecycle_status=''active''andis_primary=1'
                THROW 51000, 'TourMedia active primary index mismatch.', 1;

            DECLARE @lookupIndexId INT = INDEXPROPERTY(
                OBJECT_ID(N'commerce.TourMedia'),
                N'IX_TourMedia_TourLifecycleOrder', N'IndexId');
            IF @lookupIndexId IS NULL
               OR INDEXPROPERTY(OBJECT_ID(N'commerce.TourMedia'),
                    N'IX_TourMedia_TourLifecycleOrder', N'IsUnique') <> 0
               OR INDEX_COL(N'commerce.TourMedia', @lookupIndexId, 1) <> N'tour_id'
               OR INDEX_COL(N'commerce.TourMedia', @lookupIndexId, 2) <> N'lifecycle_status'
               OR INDEX_COL(N'commerce.TourMedia', @lookupIndexId, 3) <> N'sort_order'
               OR INDEX_COL(N'commerce.TourMedia', @lookupIndexId, 4) <> N'tour_media_id'
               OR INDEX_COL(N'commerce.TourMedia', @lookupIndexId, 5) IS NOT NULL
                THROW 51000, 'TourMedia gallery lookup index mismatch.', 1;
            """);
    }

    private static async Task<IReadOnlyList<string>> ReadTourMediaInventoryAsync(
        SqlServerTestDatabase database)
    {
        const string commandText = """
            WITH Inventory AS (
                SELECT CONCAT(N'TABLE|', SCHEMA_NAME(object.schema_id), N'.', object.name)
                    COLLATE DATABASE_DEFAULT AS item
                FROM sys.objects AS object
                WHERE object.object_id = OBJECT_ID(N'commerce.TourMedia')
                  AND object.type = N'U'

                UNION ALL

                SELECT CONCAT(
                    N'COLUMN|', column_info.column_id, N'|', column_info.name,
                    N'|', type_info.name, N'|', column_info.max_length,
                    N'|', column_info.precision, N'|', column_info.scale,
                    N'|', column_info.is_nullable, N'|', column_info.is_identity,
                    N'|', column_info.is_computed,
                    N'|', COALESCE(column_info.collation_name, N'<NULL>'),
                    N'|', COALESCE(LOWER(REPLACE(REPLACE(REPLACE(
                        default_info.definition, N'(', N''), N')', N''), N' ', N'')), N'<NULL>'))
                    COLLATE DATABASE_DEFAULT
                FROM sys.columns AS column_info
                JOIN sys.types AS type_info
                    ON type_info.user_type_id = column_info.user_type_id
                LEFT JOIN sys.default_constraints AS default_info
                    ON default_info.parent_object_id = column_info.object_id
                    AND default_info.parent_column_id = column_info.column_id
                WHERE column_info.object_id = OBJECT_ID(N'commerce.TourMedia')

                UNION ALL

                SELECT CONCAT(N'KEY|', key_info.name, N'|', key_info.type,
                    N'|', key_info.unique_index_id) COLLATE DATABASE_DEFAULT
                FROM sys.key_constraints AS key_info
                WHERE key_info.parent_object_id = OBJECT_ID(N'commerce.TourMedia')

                UNION ALL

                SELECT CONCAT(
                    N'FOREIGN_KEY|', foreign_key.name,
                    N'|', SCHEMA_NAME(referenced.schema_id), N'.', referenced.name,
                    N'|', foreign_key.delete_referential_action,
                    N'|', foreign_key.update_referential_action,
                    N'|', foreign_key.is_disabled, N'|', foreign_key.is_not_trusted,
                    N'|', COL_NAME(column_map.parent_object_id, column_map.parent_column_id),
                    N'->', COL_NAME(column_map.referenced_object_id, column_map.referenced_column_id))
                    COLLATE DATABASE_DEFAULT
                FROM sys.foreign_keys AS foreign_key
                JOIN sys.objects AS referenced
                    ON referenced.object_id = foreign_key.referenced_object_id
                JOIN sys.foreign_key_columns AS column_map
                    ON column_map.constraint_object_id = foreign_key.object_id
                WHERE foreign_key.parent_object_id = OBJECT_ID(N'commerce.TourMedia')

                UNION ALL

                SELECT CONCAT(
                    N'CHECK|', check_info.name, N'|', check_info.is_disabled,
                    N'|', check_info.is_not_trusted, N'|', LOWER(REPLACE(REPLACE(
                        REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(
                            OBJECT_DEFINITION(check_info.object_id),
                            N'[', N''), N']', N''), N'(', N''), N')', N''),
                            N' ', N''), CHAR(13), N''), CHAR(10), N'')))
                    COLLATE DATABASE_DEFAULT
                FROM sys.check_constraints AS check_info
                WHERE check_info.parent_object_id = OBJECT_ID(N'commerce.TourMedia')

                UNION ALL

                SELECT CONCAT(
                    N'INDEX|', index_info.name, N'|', index_info.type,
                    N'|', index_info.is_unique, N'|', index_info.is_primary_key,
                    N'|', index_info.is_unique_constraint,
                    N'|', index_info.is_disabled, N'|', index_info.has_filter,
                    N'|', COALESCE(LOWER(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(
                        index_info.filter_definition,
                        N'[', N''), N']', N''), N'(', N''), N')', N''), N' ', N'')), N'<NULL>'),
                    N'|', STUFF((
                        SELECT N',' + COL_NAME(index_column.object_id, index_column.column_id)
                            + CASE WHEN index_column.is_descending_key = 1 THEN N':DESC' ELSE N':ASC' END
                        FROM sys.index_columns AS index_column
                        WHERE index_column.object_id = index_info.object_id
                          AND index_column.index_id = index_info.index_id
                          AND index_column.key_ordinal > 0
                        ORDER BY index_column.key_ordinal
                        FOR XML PATH(N''), TYPE).value(N'.', N'nvarchar(max)'), 1, 1, N''),
                    N'|', STUFF((
                        SELECT N',' + COL_NAME(index_column.object_id, index_column.column_id)
                        FROM sys.index_columns AS index_column
                        WHERE index_column.object_id = index_info.object_id
                          AND index_column.index_id = index_info.index_id
                          AND index_column.is_included_column = 1
                        ORDER BY index_column.index_column_id
                        FOR XML PATH(N''), TYPE).value(N'.', N'nvarchar(max)'), 1, 1, N''))
                    COLLATE DATABASE_DEFAULT
                FROM sys.indexes AS index_info
                WHERE index_info.object_id = OBJECT_ID(N'commerce.TourMedia')
                  AND index_info.index_id > 0
                  AND index_info.is_hypothetical = 0
            )
            SELECT item FROM Inventory ORDER BY item;
            """;

        return await ReadStringsAsync(database, commandText);
    }

    private static async Task<IReadOnlyList<string>> ReadLegacyStateAsync(
        SqlServerTestDatabase database)
    {
        const string commandText = """
            SELECT item
            FROM (
                SELECT CONCAT(
                    N'TOUR|', tour_id, N'|', operator_user_id, N'|', title,
                    N'|', destination, N'|', base_price, N'|', duration_days,
                    N'|', status, N'|', reviewed_by,
                    N'|', CONVERT(NVARCHAR(33), reviewed_at, 126),
                    N'|', CONVERT(NVARCHAR(33), published_at, 126)) AS item
                FROM commerce.Tours

                UNION ALL

                SELECT CONCAT(
                    N'SCHEDULE|', schedule_id, N'|', tour_id,
                    N'|', CONVERT(NVARCHAR(33), start_datetime, 126),
                    N'|', CONVERT(NVARCHAR(33), end_datetime, 126),
                    N'|', total_capacity, N'|', reserved_capacity, N'|', status)
                FROM commerce.TourSchedules

                UNION ALL

                SELECT CONCAT(
                    N'POI|', poi_id, N'|', category_id, N'|', name,
                    N'|', latitude, N'|', longitude)
                FROM catalog.POIs

                UNION ALL

                SELECT CONCAT(
                    N'POI_PHOTO|', photo_id, N'|', poi_id, N'|', url,
                    N'|', caption, N'|', sort_order)
                FROM catalog.POIPhotos

                UNION ALL SELECT CONCAT(N'IDENTITY|Tours|', IDENT_CURRENT(N'commerce.Tours'))
                UNION ALL SELECT CONCAT(N'IDENTITY|TourSchedules|', IDENT_CURRENT(N'commerce.TourSchedules'))
                UNION ALL SELECT CONCAT(N'IDENTITY|POIs|', IDENT_CURRENT(N'catalog.POIs'))
                UNION ALL SELECT CONCAT(N'IDENTITY|POIPhotos|', IDENT_CURRENT(N'catalog.POIPhotos'))
            ) AS state
            ORDER BY item;
            """;

        return await ReadStringsAsync(database, commandText);
    }

    private static async Task<IReadOnlyList<string>> ReadStringsAsync(
        SqlServerTestDatabase database,
        string commandText)
    {
        await using var connection = new SqlConnection(database.ConnectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand(commandText, connection);
        await using var reader = await command.ExecuteReaderAsync();

        var values = new List<string>();
        while (await reader.ReadAsync())
        {
            values.Add(reader.GetString(0));
        }

        return values;
    }

    private static async Task<int> ReadIntAsync(
        SqlServerTestDatabase database,
        string commandText)
    {
        await using var connection = new SqlConnection(database.ConnectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand(commandText, connection);
        return Convert.ToInt32(await command.ExecuteScalarAsync());
    }

    private static async Task<string?> ReadStringAsync(
        SqlServerTestDatabase database,
        string commandText)
    {
        await using var connection = new SqlConnection(database.ConnectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand(commandText, connection);
        var value = await command.ExecuteScalarAsync();
        return value is null or DBNull ? null : Convert.ToString(value);
    }
}