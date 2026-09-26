using FluentAssertions;

using Microsoft.Data.SqlClient;

using TripMate.Api.IntegrationTests.Infrastructure;

namespace TripMate.Api.IntegrationTests.Tours;

public sealed class TourMediaManagementMigrationTests
{
    private const string Tm206MigrationFileName = "20260923_add_tour_media.sql";
    private const string Tm207MigrationFileName = "20260927_add_tour_media_management.sql";

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task FreshSchema_HasApprovedTourMediaManagementInventory()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();

        await AssertApprovedShapeAsync(database);
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task Migration_FromPostTm206Baseline_MatchesFreshInventoryAndPreservesData()
    {
        await using var freshDatabase = await SqlServerTestDatabase.CreateAsync();
        await using var upgradedDatabase = await CreatePostTm206DatabaseAsync();
        var before = await ReadPreservedStateAsync(upgradedDatabase);

        await ApplyTm207MigrationAsync(upgradedDatabase);

        await AssertApprovedShapeAsync(freshDatabase);
        await AssertApprovedShapeAsync(upgradedDatabase);
        (await ReadAffectedInventoryAsync(upgradedDatabase)).Should().Equal(
            await ReadAffectedInventoryAsync(freshDatabase),
            "a post-TM-206 database upgraded by TM-207 must match the fresh schema exactly");
        (await ReadPreservedStateAsync(upgradedDatabase)).Should().Equal(
            before,
            "the additive migration may backfill alt text but must preserve all pre-existing business data and identities");
        (await ReadStringAsync(upgradedDatabase, """
            SELECT alt_text
            FROM commerce.TourMedia
            WHERE tour_media_id = 7001;
            """)).Should().Be("Tour image");
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task Migration_FromPostTm206Baseline_IsIdempotent()
    {
        await using var database = await CreatePostTm206DatabaseAsync();

        await ApplyTm207MigrationAsync(database);
        var firstInventory = await ReadAffectedInventoryAsync(database);
        var firstState = await ReadPreservedStateAsync(database);

        await ApplyTm207MigrationAsync(database);

        (await ReadAffectedInventoryAsync(database)).Should().Equal(firstInventory);
        (await ReadPreservedStateAsync(database)).Should().Equal(firstState);
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task MigrationChain_AfterTm207_CanReplayTm206AndTm207WithoutDrift()
    {
        await using var database = await CreatePostTm206DatabaseAsync();

        await ApplyTm207MigrationAsync(database);
        var firstInventory = await ReadAffectedInventoryAsync(database);
        var firstState = await ReadPreservedStateAsync(database);

        await ApplyMigrationAsync(database, Tm206MigrationFileName);
        await ApplyTm207MigrationAsync(database);

        await AssertApprovedShapeAsync(database);
        (await ReadAffectedInventoryAsync(database)).Should().Equal(firstInventory);
        (await ReadPreservedStateAsync(database)).Should().Equal(firstState);
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task FreshSchema_ThenFullMigrationChain_IsIdempotent()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        var initialInventory = await ReadAffectedInventoryAsync(database);

        await ApplyMigrationAsync(database, Tm206MigrationFileName);
        await ApplyTm207MigrationAsync(database);

        await AssertApprovedShapeAsync(database);
        (await ReadAffectedInventoryAsync(database)).Should().Equal(initialInventory);
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task Migration_WhenAltTextColumnHasWrongShape_RollsBackWithoutPartialObjects()
    {
        await using var database = await CreatePostTm206DatabaseAsync();
        await database.ExecuteNonQueryAsync("""
            ALTER TABLE commerce.TourMedia ADD alt_text INT NULL;
            """);
        var before = await ReadPreservedStateAsync(database);

        Func<Task> action = () => ApplyTm207MigrationAsync(database);

        await action.Should().ThrowAsync<SqlException>();
        (await ReadStringAsync(database, """
            SELECT CONCAT(TYPE_NAME(user_type_id), N'|', max_length, N'|', is_nullable)
            FROM sys.columns
            WHERE object_id = OBJECT_ID(N'commerce.TourMedia')
              AND name = N'alt_text';
            """)).Should().Be("int|4|1");
        (await ObjectExistsAsync(database, "commerce.TourMediaUploadOperations")).Should().BeFalse();
        (await ObjectExistsAsync(database, "commerce.TourMediaCleanupOutbox")).Should().BeFalse();
        (await ReadPreservedStateAsync(database)).Should().Equal(before);
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task Migration_WhenUploadOperationTableHasWrongShape_RollsBackWithoutReplacingIt()
    {
        await using var database = await CreatePostTm206DatabaseAsync();
        await database.ExecuteNonQueryAsync("""
            CREATE TABLE commerce.TourMediaUploadOperations (
                upload_operation_id INT NOT NULL
                    CONSTRAINT PK_TourMediaUploadOperations PRIMARY KEY
            );
            """);

        Func<Task> action = () => ApplyTm207MigrationAsync(database);

        await action.Should().ThrowAsync<SqlException>();
        (await ReadIntAsync(database, """
            SELECT COUNT(*)
            FROM sys.columns
            WHERE object_id = OBJECT_ID(N'commerce.TourMediaUploadOperations');
            """)).Should().Be(1);
        (await ReadIntAsync(database, """
            SELECT COUNT(*)
            FROM sys.columns
            WHERE object_id = OBJECT_ID(N'commerce.TourMedia')
              AND name = N'alt_text';
            """)).Should().Be(0, "the failed transaction must not leave the earlier column addition behind");
        (await ObjectExistsAsync(database, "commerce.TourMediaCleanupOutbox")).Should().BeFalse();
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task Migration_WhenCleanupOutboxHasWrongShape_RollsBackWithoutReplacingIt()
    {
        await using var database = await CreatePostTm206DatabaseAsync();
        await database.ExecuteNonQueryAsync("""
            CREATE TABLE commerce.TourMediaCleanupOutbox (
                cleanup_outbox_id INT NOT NULL
                    CONSTRAINT PK_TourMediaCleanupOutbox PRIMARY KEY
            );
            """);

        Func<Task> action = () => ApplyTm207MigrationAsync(database);

        await action.Should().ThrowAsync<SqlException>();
        (await ReadIntAsync(database, """
            SELECT COUNT(*)
            FROM sys.columns
            WHERE object_id = OBJECT_ID(N'commerce.TourMediaCleanupOutbox');
            """)).Should().Be(1);
        (await ObjectExistsAsync(database, "commerce.TourMediaUploadOperations")).Should().BeFalse();
        (await ReadIntAsync(database, """
            SELECT COUNT(*)
            FROM sys.columns
            WHERE object_id = OBJECT_ID(N'commerce.TourMedia')
              AND name = N'alt_text';
            """)).Should().Be(0);
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task UniqueContracts_RejectDuplicateProviderIdsAndUploadOperationScope()
    {
        await using var database = await CreatePostTm206DatabaseAsync();
        await ApplyTm207MigrationAsync(database);

        Func<Task> duplicateMediaPublicId = () => database.ExecuteNonQueryAsync("""
            INSERT INTO commerce.TourMedia
                (tour_id, cloudinary_public_id, delivery_url, caption, alt_text,
                 sort_order, is_primary)
            VALUES
                (5001, N'tripmate/tours/5001/legacy-active',
                 N'https://res.cloudinary.com/tripmate/image/upload/duplicate.webp',
                 NULL, N'Duplicate', 2, 0);
            """);
        (await duplicateMediaPublicId.Should().ThrowAsync<SqlException>())
            .Which.Number.Should().BeOneOf(2601, 2627);

        await database.ExecuteNonQueryAsync(ValidPendingUploadOperationSql(
            "11111111-1111-1111-1111-111111111111",
            "tripmate/tours/5001/upload-a"));

        Func<Task> duplicateOperationKey = () => database.ExecuteNonQueryAsync(
            ValidPendingUploadOperationSql(
                "11111111-1111-1111-1111-111111111111",
                "tripmate/tours/5001/upload-b"));
        (await duplicateOperationKey.Should().ThrowAsync<SqlException>())
            .Which.Number.Should().BeOneOf(2601, 2627);

        Func<Task> duplicateOperationPublicId = () => database.ExecuteNonQueryAsync(
            ValidPendingUploadOperationSql(
                "22222222-2222-2222-2222-222222222222",
                "tripmate/tours/5001/upload-a"));
        (await duplicateOperationPublicId.Should().ThrowAsync<SqlException>())
            .Which.Number.Should().BeOneOf(2601, 2627);

        await database.ExecuteNonQueryAsync("""
            INSERT INTO commerce.TourMediaCleanupOutbox
                (tour_media_id, cloudinary_public_id, not_before_at)
            VALUES
                (7002, N'tripmate/tours/5001/legacy-deleted',
                 DATEADD(DAY, 30, SYSUTCDATETIME()));
            """);

        Func<Task> duplicateCleanupPublicId = () => database.ExecuteNonQueryAsync("""
            INSERT INTO commerce.TourMediaCleanupOutbox
                (tour_media_id, cloudinary_public_id, not_before_at)
            VALUES
                (NULL, N'tripmate/tours/5001/legacy-deleted',
                 DATEADD(DAY, 30, SYSUTCDATETIME()));
            """);
        (await duplicateCleanupPublicId.Should().ThrowAsync<SqlException>())
            .Which.Number.Should().BeOneOf(2601, 2627);

        Func<Task> duplicateCleanupMedia = () => database.ExecuteNonQueryAsync("""
            INSERT INTO commerce.TourMediaCleanupOutbox
                (tour_media_id, cloudinary_public_id, not_before_at)
            VALUES
                (7002, N'tripmate/tours/5001/other-cleanup',
                 DATEADD(DAY, 30, SYSUTCDATETIME()));
            """);
        (await duplicateCleanupMedia.Should().ThrowAsync<SqlException>())
            .Which.Number.Should().BeOneOf(2601, 2627);
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task OperationAndCleanupChecks_RejectInvalidStateAndRetryCombinations()
    {
        await using var database = await CreatePostTm206DatabaseAsync();
        await ApplyTm207MigrationAsync(database);

        Func<Task> completedWithoutMedia = () => database.ExecuteNonQueryAsync("""
            INSERT INTO commerce.TourMediaUploadOperations
                (actor_user_id, tour_id, idempotency_key, payload_fingerprint,
                 cloudinary_public_id, operation_status, provider_uploaded_at,
                 completed_at)
            VALUES
                (101, 5001, '33333333-3333-3333-3333-333333333333',
                 'AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA',
                 N'tripmate/tours/5001/invalid-completed', 'Completed',
                 SYSUTCDATETIME(), SYSUTCDATETIME());
            """);
        (await completedWithoutMedia.Should().ThrowAsync<SqlException>())
            .Which.Number.Should().Be(547);

        Func<Task> malformedFingerprint = () => database.ExecuteNonQueryAsync(
            ValidPendingUploadOperationSql(
                "44444444-4444-4444-4444-444444444444",
                "tripmate/tours/5001/invalid-hash",
                fingerprint: "NOT-A-SHA256-FINGERPRINT"));
        (await malformedFingerprint.Should().ThrowAsync<SqlException>())
            .Which.Number.Should().Be(547);

        Func<Task> pendingWithLease = () => database.ExecuteNonQueryAsync("""
            INSERT INTO commerce.TourMediaCleanupOutbox
                (tour_media_id, cloudinary_public_id, cleanup_status,
                 not_before_at, attempt_count, max_attempts,
                 lease_token, lease_expires_at)
            VALUES
                (7002, N'tripmate/tours/5001/legacy-deleted', 'Pending',
                 DATEADD(DAY, 30, SYSUTCDATETIME()), 0, 8,
                 NEWID(), DATEADD(MINUTE, 5, SYSUTCDATETIME()));
            """);
        (await pendingWithLease.Should().ThrowAsync<SqlException>())
            .Which.Number.Should().Be(547);

        Func<Task> attemptsPastBound = () => database.ExecuteNonQueryAsync("""
            INSERT INTO commerce.TourMediaCleanupOutbox
                (tour_media_id, cloudinary_public_id, cleanup_status,
                 not_before_at, attempt_count, max_attempts)
            VALUES
                (7002, N'tripmate/tours/5001/legacy-deleted', 'Pending',
                 DATEADD(DAY, 30, SYSUTCDATETIME()), 9, 8);
            """);
        (await attemptsPastBound.Should().ThrowAsync<SqlException>())
            .Which.Number.Should().Be(547);
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task CleanupOutbox_WhenTourMediaIsPhysicallyDeleted_PreservesProviderIdentity()
    {
        await using var database = await CreatePostTm206DatabaseAsync();
        await ApplyTm207MigrationAsync(database);
        await database.ExecuteNonQueryAsync("""
            INSERT INTO commerce.TourMediaCleanupOutbox
                (tour_media_id, cloudinary_public_id, not_before_at)
            VALUES
                (7002, N'tripmate/tours/5001/legacy-deleted',
                 DATEADD(DAY, 30, SYSUTCDATETIME()));

            DELETE FROM commerce.TourSchedules WHERE tour_id = 5001;
            DELETE FROM commerce.Tours WHERE tour_id = 5001;
            """);

        (await ReadStringAsync(database, """
            SELECT CONCAT(
                COALESCE(CONVERT(NVARCHAR(30), tour_media_id), N'<NULL>'), N'|',
                cloudinary_public_id, N'|', cleanup_status)
            FROM commerce.TourMediaCleanupOutbox;
            """)).Should().Be(
                "<NULL>|tripmate/tours/5001/legacy-deleted|Pending",
                "provider cleanup must survive the approved physical parent-Tour cascade");
    }

    private static async Task<SqlServerTestDatabase> CreatePostTm206DatabaseAsync()
    {
        var database = await SqlServerTestDatabase.CreateEmptyAsync();
        var fixtureRoot = Path.Combine(AppContext.BaseDirectory, "Database", "Fixtures");
        var migrationRoot = Path.Combine(AppContext.BaseDirectory, "Database", "migrations");
        await database.ExecuteScriptAsync(Path.Combine(fixtureRoot, "tm206_pre_migration_schema.sql"));
        await database.ExecuteScriptAsync(Path.Combine(migrationRoot, Tm206MigrationFileName));
        await database.ExecuteScriptAsync(Path.Combine(fixtureRoot, "tm207_pre_migration_data.sql"));
        return database;
    }

    private static Task ApplyTm207MigrationAsync(SqlServerTestDatabase database)
        => ApplyMigrationAsync(database, Tm207MigrationFileName);

    private static Task ApplyMigrationAsync(
        SqlServerTestDatabase database,
        string migrationFileName)
    {
        var migrationPath = Path.Combine(
            AppContext.BaseDirectory,
            "Database",
            "migrations",
            migrationFileName);
        return database.ExecuteScriptAsync(migrationPath);
    }

    private static Task AssertApprovedShapeAsync(SqlServerTestDatabase database) =>
        database.ExecuteNonQueryAsync("""
            IF COL_LENGTH(N'commerce.TourMedia', N'alt_text') IS NULL
                THROW 51000, 'TourMedia.alt_text is missing.', 1;

            IF NOT EXISTS (
                SELECT 1
                FROM sys.columns AS column_info
                JOIN sys.types AS type_info
                    ON type_info.user_type_id = column_info.user_type_id
                WHERE column_info.object_id = OBJECT_ID(N'commerce.TourMedia')
                  AND column_info.name = N'alt_text'
                  AND type_info.name = N'nvarchar'
                  AND column_info.max_length = 1000
                  AND column_info.is_nullable = 0
                  AND column_info.collation_name = N'Vietnamese_100_CI_AS')
                THROW 51000, 'TourMedia.alt_text shape mismatch.', 1;

            IF NOT EXISTS (
                SELECT 1 FROM sys.indexes
                WHERE object_id = OBJECT_ID(N'commerce.TourMedia')
                  AND name = N'UX_TourMedia_CloudinaryPublicId'
                  AND is_unique = 1 AND has_filter = 0)
               OR INDEX_COL(N'commerce.TourMedia',
                    INDEXPROPERTY(OBJECT_ID(N'commerce.TourMedia'),
                        N'UX_TourMedia_CloudinaryPublicId', N'IndexId'), 1)
                    <> N'cloudinary_public_id'
               OR INDEX_COL(N'commerce.TourMedia',
                    INDEXPROPERTY(OBJECT_ID(N'commerce.TourMedia'),
                        N'UX_TourMedia_CloudinaryPublicId', N'IndexId'), 2)
                    IS NOT NULL
                THROW 51000, 'TourMedia provider-id index mismatch.', 1;

            IF OBJECT_ID(N'commerce.TourMediaUploadOperations', N'U') IS NULL
                THROW 51000, 'TourMediaUploadOperations is missing.', 1;
            IF OBJECT_ID(N'commerce.TourMediaCleanupOutbox', N'U') IS NULL
                THROW 51000, 'TourMediaCleanupOutbox is missing.', 1;

            IF (SELECT COUNT(*) FROM sys.columns
                WHERE object_id = OBJECT_ID(N'commerce.TourMediaUploadOperations')) <> 12
                THROW 51000, 'TourMediaUploadOperations column count mismatch.', 1;
            IF (SELECT COUNT(*) FROM sys.columns
                WHERE object_id = OBJECT_ID(N'commerce.TourMediaCleanupOutbox')) <> 13
                THROW 51000, 'TourMediaCleanupOutbox column count mismatch.', 1;

            IF NOT EXISTS (
                SELECT 1 FROM sys.indexes
                WHERE object_id = OBJECT_ID(N'commerce.TourMediaUploadOperations')
                  AND name = N'UX_TourMediaUploadOperations_ActorTourKey'
                  AND is_unique = 1)
                THROW 51000, 'Upload operation idempotency index is missing.', 1;
            IF NOT EXISTS (
                SELECT 1 FROM sys.indexes
                WHERE object_id = OBJECT_ID(N'commerce.TourMediaUploadOperations')
                  AND name = N'UX_TourMediaUploadOperations_PublicId'
                  AND is_unique = 1)
                THROW 51000, 'Upload operation provider-id index is missing.', 1;
            IF NOT EXISTS (
                SELECT 1 FROM sys.indexes
                WHERE object_id = OBJECT_ID(N'commerce.TourMediaCleanupOutbox')
                  AND name = N'IX_TourMediaCleanupOutbox_Due')
                THROW 51000, 'Cleanup due-work index is missing.', 1;

            IF NOT EXISTS (
                SELECT 1
                FROM sys.foreign_keys
                WHERE parent_object_id = OBJECT_ID(N'commerce.TourMediaCleanupOutbox')
                  AND name = N'FK_TourMediaCleanupOutbox_TourMedia'
                  AND delete_referential_action_desc = N'SET_NULL'
                  AND is_disabled = 0 AND is_not_trusted = 0)
                THROW 51000, 'Cleanup media FK must preserve work with SET NULL.', 1;

            DECLARE @requiredChecks TABLE (table_name SYSNAME, constraint_name SYSNAME);
            INSERT INTO @requiredChecks (table_name, constraint_name) VALUES
                (N'TourMediaUploadOperations', N'CK_TourMediaUploadOperations_Fingerprint'),
                (N'TourMediaUploadOperations', N'CK_TourMediaUploadOperations_Status'),
                (N'TourMediaUploadOperations', N'CK_TourMediaUploadOperations_State'),
                (N'TourMediaCleanupOutbox', N'CK_TourMediaCleanupOutbox_Status'),
                (N'TourMediaCleanupOutbox', N'CK_TourMediaCleanupOutbox_Attempts'),
                (N'TourMediaCleanupOutbox', N'CK_TourMediaCleanupOutbox_State');

            IF EXISTS (
                SELECT 1
                FROM @requiredChecks AS required
                LEFT JOIN sys.check_constraints AS actual
                    ON actual.parent_object_id = OBJECT_ID(
                        N'commerce.' + required.table_name)
                    AND actual.name = required.constraint_name
                    AND actual.is_disabled = 0
                    AND actual.is_not_trusted = 0
                WHERE actual.object_id IS NULL)
                THROW 51000, 'A required trusted TM-207 check is missing.', 1;
            """);

    private static async Task<IReadOnlyList<string>> ReadAffectedInventoryAsync(
        SqlServerTestDatabase database)
    {
        const string commandText = """
            WITH ManagedObjects AS (
                SELECT object_id, name
                FROM sys.objects
                WHERE object_id IN (
                    OBJECT_ID(N'commerce.TourMedia'),
                    OBJECT_ID(N'commerce.TourMediaUploadOperations'),
                    OBJECT_ID(N'commerce.TourMediaCleanupOutbox'))
                  AND type = N'U'
            ), Inventory AS (
                SELECT CONCAT(N'TABLE|', managed.name) COLLATE DATABASE_DEFAULT AS item
                FROM ManagedObjects AS managed

                UNION ALL

                SELECT CONCAT(
                    N'COLUMN|', managed.name, N'|', column_info.column_id,
                    N'|', column_info.name, N'|', type_info.name,
                    N'|', column_info.max_length, N'|', column_info.precision,
                    N'|', column_info.scale, N'|', column_info.is_nullable,
                    N'|', column_info.is_identity,
                    N'|', COALESCE(column_info.collation_name, N'<NULL>'),
                    N'|', COALESCE(LOWER(REPLACE(REPLACE(REPLACE(
                        default_info.definition, N'(', N''), N')', N''), N' ', N'')),
                        N'<NULL>')) COLLATE DATABASE_DEFAULT
                FROM ManagedObjects AS managed
                JOIN sys.columns AS column_info
                    ON column_info.object_id = managed.object_id
                JOIN sys.types AS type_info
                    ON type_info.user_type_id = column_info.user_type_id
                LEFT JOIN sys.default_constraints AS default_info
                    ON default_info.parent_object_id = column_info.object_id
                    AND default_info.parent_column_id = column_info.column_id

                UNION ALL

                SELECT CONCAT(
                    N'KEY|', managed.name, N'|', key_info.name,
                    N'|', key_info.type) COLLATE DATABASE_DEFAULT
                FROM ManagedObjects AS managed
                JOIN sys.key_constraints AS key_info
                    ON key_info.parent_object_id = managed.object_id

                UNION ALL

                SELECT CONCAT(
                    N'FOREIGN_KEY|', managed.name, N'|', foreign_key.name,
                    N'|', SCHEMA_NAME(referenced.schema_id), N'.', referenced.name,
                    N'|', foreign_key.delete_referential_action,
                    N'|', foreign_key.update_referential_action,
                    N'|', foreign_key.is_disabled, N'|', foreign_key.is_not_trusted,
                    N'|', COL_NAME(column_map.parent_object_id, column_map.parent_column_id),
                    N'->', COL_NAME(column_map.referenced_object_id,
                        column_map.referenced_column_id)) COLLATE DATABASE_DEFAULT
                FROM ManagedObjects AS managed
                JOIN sys.foreign_keys AS foreign_key
                    ON foreign_key.parent_object_id = managed.object_id
                JOIN sys.objects AS referenced
                    ON referenced.object_id = foreign_key.referenced_object_id
                JOIN sys.foreign_key_columns AS column_map
                    ON column_map.constraint_object_id = foreign_key.object_id

                UNION ALL

                SELECT CONCAT(
                    N'CHECK|', managed.name, N'|', check_info.name,
                    N'|', check_info.is_disabled, N'|', check_info.is_not_trusted,
                    N'|', LOWER(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(
                        OBJECT_DEFINITION(check_info.object_id),
                        N'[', N''), N']', N''), N'(', N''), N')', N''),
                        N' ', N''), CHAR(13), N''), CHAR(10), N'')))
                    COLLATE DATABASE_DEFAULT
                FROM ManagedObjects AS managed
                JOIN sys.check_constraints AS check_info
                    ON check_info.parent_object_id = managed.object_id

                UNION ALL

                SELECT CONCAT(
                    N'INDEX|', managed.name, N'|', index_info.name,
                    N'|', index_info.type, N'|', index_info.is_unique,
                    N'|', index_info.is_primary_key,
                    N'|', index_info.is_disabled, N'|', index_info.has_filter,
                    N'|', COALESCE(LOWER(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(
                        index_info.filter_definition,
                        N'[', N''), N']', N''), N'(', N''), N')', N''), N' ', N'')),
                        N'<NULL>'),
                    N'|', STUFF((
                        SELECT N',' + COL_NAME(index_column.object_id,
                            index_column.column_id)
                            + CASE WHEN index_column.is_descending_key = 1
                                THEN N':DESC' ELSE N':ASC' END
                        FROM sys.index_columns AS index_column
                        WHERE index_column.object_id = index_info.object_id
                          AND index_column.index_id = index_info.index_id
                          AND index_column.key_ordinal > 0
                        ORDER BY index_column.key_ordinal
                        FOR XML PATH(N''), TYPE).value(N'.', N'nvarchar(max)'),
                        1, 1, N'')) COLLATE DATABASE_DEFAULT
                FROM ManagedObjects AS managed
                JOIN sys.indexes AS index_info
                    ON index_info.object_id = managed.object_id
                WHERE index_info.index_id > 0
                  AND index_info.is_hypothetical = 0
            )
            SELECT item FROM Inventory ORDER BY item;
            """;

        return await ReadStringsAsync(database, commandText);
    }

    private static async Task<IReadOnlyList<string>> ReadPreservedStateAsync(
        SqlServerTestDatabase database)
    {
        const string commandText = """
            SELECT item
            FROM (
                SELECT CONCAT(
                    N'TOUR|', tour_id, N'|', operator_user_id, N'|', title,
                    N'|', destination, N'|', base_price, N'|', duration_days,
                    N'|', status, N'|', reviewed_by) AS item
                FROM commerce.Tours

                UNION ALL

                SELECT CONCAT(
                    N'MEDIA|', tour_media_id, N'|', tour_id,
                    N'|', cloudinary_public_id, N'|', delivery_url,
                    N'|', caption, N'|', sort_order, N'|', is_primary,
                    N'|', lifecycle_status,
                    N'|', CONVERT(NVARCHAR(33), created_at, 126),
                    N'|', CONVERT(NVARCHAR(33), updated_at, 126),
                    N'|', COALESCE(CONVERT(NVARCHAR(33), deleted_at, 126), N'<NULL>'))
                FROM commerce.TourMedia

                UNION ALL

                SELECT CONCAT(
                    N'POI_PHOTO|', photo_id, N'|', poi_id, N'|', url,
                    N'|', caption, N'|', sort_order)
                FROM catalog.POIPhotos

                UNION ALL SELECT CONCAT(N'IDENTITY|Tours|', IDENT_CURRENT(N'commerce.Tours'))
                UNION ALL SELECT CONCAT(N'IDENTITY|TourMedia|', IDENT_CURRENT(N'commerce.TourMedia'))
                UNION ALL SELECT CONCAT(N'IDENTITY|POIPhotos|', IDENT_CURRENT(N'catalog.POIPhotos'))
            ) AS state
            ORDER BY item;
            """;

        return await ReadStringsAsync(database, commandText);
    }

    private static string ValidPendingUploadOperationSql(
        string idempotencyKey,
        string publicId,
        string fingerprint = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA") =>
        $"""
        INSERT INTO commerce.TourMediaUploadOperations
            (actor_user_id, tour_id, idempotency_key, payload_fingerprint,
             cloudinary_public_id)
        VALUES
            (101, 5001, '{idempotencyKey}', '{fingerprint}', N'{publicId}');
        """;

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

    private static async Task<bool> ObjectExistsAsync(
        SqlServerTestDatabase database,
        string qualifiedName) =>
        await ReadIntAsync(database, $"SELECT CASE WHEN OBJECT_ID(N'{qualifiedName}', N'U') IS NULL THEN 0 ELSE 1 END;") == 1;
}