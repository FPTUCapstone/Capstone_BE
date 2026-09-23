/*
    TM-206: Tour-owned Cloudinary image metadata.
    Additive and idempotent. Raw image binaries are never stored in SQL Server.
    Normal media removal is a lifecycle soft delete; ON DELETE CASCADE applies
    only when the parent Tour is physically deleted.
*/
SET XACT_ABORT ON;
GO

BEGIN TRY
    BEGIN TRANSACTION;

    IF OBJECT_ID(N'commerce.Tours', N'U') IS NULL
        THROW 51000, 'TM-206 migration requires commerce.Tours.', 1;

    IF OBJECT_ID(N'commerce.TourMedia', N'U') IS NULL
    BEGIN
        IF OBJECT_ID(N'commerce.TourMedia') IS NOT NULL
            THROW 51000, 'TM-206 TourMedia object has the wrong type.', 1;

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
            CONSTRAINT CK_TourMedia_Lifecycle CHECK (
                lifecycle_status IN ('Active','Deleted')),
            CONSTRAINT CK_TourMedia_DeletedAt CHECK (
                (lifecycle_status = 'Active' AND deleted_at IS NULL)
                OR (lifecycle_status = 'Deleted' AND deleted_at IS NOT NULL))
        );

        CREATE UNIQUE INDEX UX_TourMedia_ActiveSortOrder
            ON commerce.TourMedia(tour_id, sort_order)
            WHERE lifecycle_status = 'Active';
        CREATE UNIQUE INDEX UX_TourMedia_ActivePrimary
            ON commerce.TourMedia(tour_id)
            WHERE lifecycle_status = 'Active' AND is_primary = 1;
        CREATE INDEX IX_TourMedia_TourLifecycleOrder
            ON commerce.TourMedia(
                tour_id, lifecycle_status, sort_order, tour_media_id);
    END;

    DECLARE @databaseCollation SYSNAME = CONVERT(
        SYSNAME, DATABASEPROPERTYEX(DB_NAME(), N'Collation'));

    IF (SELECT COUNT(*) FROM sys.columns
        WHERE object_id = OBJECT_ID(N'commerce.TourMedia')) <> 11
        THROW 51000, 'TM-206 TourMedia column count mismatch; manual reconciliation is required.', 1;

    IF EXISTS (
        SELECT expected.name
        FROM (VALUES
            (N'tour_media_id', N'bigint', 8, 19, 0, 0, 1, 0,
                CAST(NULL AS SYSNAME), CAST(NULL AS SYSNAME), CAST(NULL AS NVARCHAR(128))),
            (N'tour_id', N'bigint', 8, 19, 0, 0, 0, 0,
                NULL, NULL, NULL),
            (N'cloudinary_public_id', N'nvarchar', 1000, 0, 0, 0, 0, 0,
                @databaseCollation, NULL, NULL),
            (N'delivery_url', N'nvarchar', 2000, 0, 0, 0, 0, 0,
                @databaseCollation, NULL, NULL),
            (N'caption', N'nvarchar', 1000, 0, 0, 1, 0, 0,
                @databaseCollation, NULL, NULL),
            (N'sort_order', N'int', 4, 10, 0, 0, 0, 0,
                NULL, NULL, NULL),
            (N'is_primary', N'bit', 1, 1, 0, 0, 0, 0,
                NULL, N'DF_TourMedia_IsPrimary', N'0'),
            (N'lifecycle_status', N'varchar', 16, 0, 0, 0, 0, 0,
                @databaseCollation, N'DF_TourMedia_LifecycleStatus', N'''active'''),
            (N'created_at', N'datetime2', 8, 27, 7, 0, 0, 0,
                NULL, N'DF_TourMedia_CreatedAt', N'sysutcdatetime'),
            (N'updated_at', N'datetime2', 8, 27, 7, 0, 0, 0,
                NULL, N'DF_TourMedia_UpdatedAt', N'sysutcdatetime'),
            (N'deleted_at', N'datetime2', 8, 27, 7, 1, 0, 0,
                NULL, NULL, NULL)
        ) AS expected(
            name, type_name, max_length, precision_value, scale_value,
            is_nullable, is_identity, is_computed, collation_name,
            default_name, default_definition)
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
           OR actual.is_computed <> expected.is_computed
           OR ISNULL(actual.collation_name, N'<null>')
              <> ISNULL(expected.collation_name, N'<null>')
           OR ISNULL(default_constraint.name, N'<null>')
              <> ISNULL(expected.default_name, N'<null>')
           OR ISNULL(LOWER(REPLACE(REPLACE(REPLACE(
                default_constraint.definition, N'(', N''), N')', N''), N' ', N'')), N'<null>')
              <> ISNULL(expected.default_definition, N'<null>'))
        THROW 51000, 'TM-206 TourMedia column/default shape mismatch; manual reconciliation is required.', 1;

    IF (SELECT COUNT(*) FROM sys.default_constraints
        WHERE parent_object_id = OBJECT_ID(N'commerce.TourMedia')) <> 4
        THROW 51000, 'TM-206 TourMedia default constraint inventory mismatch.', 1;

    DECLARE @primaryKeyIndexId INT;
    SELECT @primaryKeyIndexId = key_constraint.unique_index_id
    FROM sys.key_constraints AS key_constraint
    WHERE key_constraint.parent_object_id = OBJECT_ID(N'commerce.TourMedia')
      AND key_constraint.name = N'PK_TourMedia'
      AND key_constraint.type = N'PK';

    IF (SELECT COUNT(*) FROM sys.key_constraints
        WHERE parent_object_id = OBJECT_ID(N'commerce.TourMedia')) <> 1
       OR @primaryKeyIndexId IS NULL
       OR INDEX_COL(N'commerce.TourMedia', @primaryKeyIndexId, 1) <> N'tour_media_id'
       OR INDEX_COL(N'commerce.TourMedia', @primaryKeyIndexId, 2) IS NOT NULL
       OR NOT EXISTS (
            SELECT 1 FROM sys.indexes
            WHERE object_id = OBJECT_ID(N'commerce.TourMedia')
              AND index_id = @primaryKeyIndexId
              AND type = 1 AND is_unique = 1 AND is_primary_key = 1
              AND is_disabled = 0 AND has_filter = 0)
        THROW 51000, 'TM-206 TourMedia primary key shape mismatch.', 1;

    IF (SELECT COUNT(*) FROM sys.foreign_keys
        WHERE parent_object_id = OBJECT_ID(N'commerce.TourMedia')) <> 1
       OR NOT EXISTS (
            SELECT 1
            FROM sys.foreign_keys AS foreign_key
            JOIN sys.foreign_key_columns AS column_map
              ON column_map.constraint_object_id = foreign_key.object_id
            WHERE foreign_key.parent_object_id = OBJECT_ID(N'commerce.TourMedia')
              AND foreign_key.referenced_object_id = OBJECT_ID(N'commerce.Tours')
              AND foreign_key.name = N'FK_TourMedia_Tours'
              AND foreign_key.delete_referential_action = 1
              AND foreign_key.update_referential_action = 0
              AND foreign_key.is_disabled = 0
              AND foreign_key.is_not_trusted = 0
              AND column_map.constraint_column_id = 1
              AND COL_NAME(column_map.parent_object_id, column_map.parent_column_id) = N'tour_id'
              AND COL_NAME(column_map.referenced_object_id, column_map.referenced_column_id) = N'tour_id')
        THROW 51000, 'TM-206 TourMedia cascade foreign key shape mismatch.', 1;

    IF (SELECT COUNT(*) FROM sys.check_constraints
        WHERE parent_object_id = OBJECT_ID(N'commerce.TourMedia')) <> 3
        THROW 51000, 'TM-206 TourMedia check constraint inventory mismatch.', 1;

    IF EXISTS (
        SELECT 1 FROM sys.check_constraints
        WHERE parent_object_id = OBJECT_ID(N'commerce.TourMedia')
          AND (is_disabled = 1 OR is_not_trusted = 1))
        THROW 51000, 'TM-206 TourMedia checks must be enabled and trusted.', 1;

    DECLARE @sortCheck NVARCHAR(MAX) = LOWER(REPLACE(REPLACE(REPLACE(REPLACE(
        OBJECT_DEFINITION(OBJECT_ID(N'commerce.CK_TourMedia_SortOrderPositive', N'C')),
        N'[', N''), N']', N''), N'(', N''), N')', N''));
    SET @sortCheck = REPLACE(REPLACE(REPLACE(@sortCheck, N' ', N''), CHAR(13), N''), CHAR(10), N'');
    IF @sortCheck IS NULL OR @sortCheck <> N'sort_order>0'
        THROW 51000, 'TM-206 TourMedia sort-order check mismatch.', 1;

    DECLARE @lifecycleCheck NVARCHAR(MAX) = LOWER(REPLACE(REPLACE(REPLACE(REPLACE(
        OBJECT_DEFINITION(OBJECT_ID(N'commerce.CK_TourMedia_Lifecycle', N'C')),
        N'[', N''), N']', N''), N'(', N''), N')', N''));
    SET @lifecycleCheck = REPLACE(REPLACE(REPLACE(@lifecycleCheck, N' ', N''), CHAR(13), N''), CHAR(10), N'');
    IF @lifecycleCheck IS NULL OR @lifecycleCheck NOT IN (
        N'lifecycle_status=''active''orlifecycle_status=''deleted''',
        N'lifecycle_status=''deleted''orlifecycle_status=''active''')
        THROW 51000, 'TM-206 TourMedia lifecycle check mismatch.', 1;

    DECLARE @deletedAtCheck NVARCHAR(MAX) = LOWER(REPLACE(REPLACE(REPLACE(REPLACE(
        OBJECT_DEFINITION(OBJECT_ID(N'commerce.CK_TourMedia_DeletedAt', N'C')),
        N'[', N''), N']', N''), N'(', N''), N')', N''));
    SET @deletedAtCheck = REPLACE(REPLACE(REPLACE(@deletedAtCheck, N' ', N''), CHAR(13), N''), CHAR(10), N'');
    IF @deletedAtCheck IS NULL OR @deletedAtCheck <>
        N'lifecycle_status=''active''anddeleted_atisnullorlifecycle_status=''deleted''anddeleted_atisnotnull'
        THROW 51000, 'TM-206 TourMedia deleted-at check mismatch.', 1;

    IF (SELECT COUNT(*) FROM sys.indexes
        WHERE object_id = OBJECT_ID(N'commerce.TourMedia')
          AND index_id > 0 AND is_hypothetical = 0) <> 4
        THROW 51000, 'TM-206 TourMedia index inventory mismatch.', 1;

    DECLARE @activeOrderIndexId INT = INDEXPROPERTY(
        OBJECT_ID(N'commerce.TourMedia'), N'UX_TourMedia_ActiveSortOrder', N'IndexId');
    IF @activeOrderIndexId IS NULL
       OR NOT EXISTS (
            SELECT 1 FROM sys.indexes
            WHERE object_id = OBJECT_ID(N'commerce.TourMedia')
              AND index_id = @activeOrderIndexId
              AND type = 2 AND is_unique = 1 AND is_primary_key = 0
              AND is_unique_constraint = 0 AND is_disabled = 0 AND has_filter = 1)
       OR INDEX_COL(N'commerce.TourMedia', @activeOrderIndexId, 1) <> N'tour_id'
       OR INDEX_COL(N'commerce.TourMedia', @activeOrderIndexId, 2) <> N'sort_order'
       OR INDEX_COL(N'commerce.TourMedia', @activeOrderIndexId, 3) IS NOT NULL
       OR EXISTS (SELECT 1 FROM sys.index_columns WHERE object_id = OBJECT_ID(N'commerce.TourMedia')
                  AND index_id = @activeOrderIndexId AND is_included_column = 1)
       OR LOWER(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(
            (SELECT filter_definition FROM sys.indexes
             WHERE object_id = OBJECT_ID(N'commerce.TourMedia') AND index_id = @activeOrderIndexId),
            N'[', N''), N']', N''), N'(', N''), N')', N''), N' ', N''))
            <> N'lifecycle_status=''active'''
        THROW 51000, 'TM-206 TourMedia active sort-order index shape mismatch.', 1;

    DECLARE @activePrimaryIndexId INT = INDEXPROPERTY(
        OBJECT_ID(N'commerce.TourMedia'), N'UX_TourMedia_ActivePrimary', N'IndexId');
    IF @activePrimaryIndexId IS NULL
       OR NOT EXISTS (
            SELECT 1 FROM sys.indexes
            WHERE object_id = OBJECT_ID(N'commerce.TourMedia')
              AND index_id = @activePrimaryIndexId
              AND type = 2 AND is_unique = 1 AND is_primary_key = 0
              AND is_unique_constraint = 0 AND is_disabled = 0 AND has_filter = 1)
       OR INDEX_COL(N'commerce.TourMedia', @activePrimaryIndexId, 1) <> N'tour_id'
       OR INDEX_COL(N'commerce.TourMedia', @activePrimaryIndexId, 2) IS NOT NULL
       OR EXISTS (SELECT 1 FROM sys.index_columns WHERE object_id = OBJECT_ID(N'commerce.TourMedia')
                  AND index_id = @activePrimaryIndexId AND is_included_column = 1)
       OR LOWER(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(
            (SELECT filter_definition FROM sys.indexes
             WHERE object_id = OBJECT_ID(N'commerce.TourMedia') AND index_id = @activePrimaryIndexId),
            N'[', N''), N']', N''), N'(', N''), N')', N''), N' ', N''))
            <> N'lifecycle_status=''active''andis_primary=1'
        THROW 51000, 'TM-206 TourMedia active primary index shape mismatch.', 1;

    DECLARE @lookupIndexId INT = INDEXPROPERTY(
        OBJECT_ID(N'commerce.TourMedia'), N'IX_TourMedia_TourLifecycleOrder', N'IndexId');
    IF @lookupIndexId IS NULL
       OR NOT EXISTS (
            SELECT 1 FROM sys.indexes
            WHERE object_id = OBJECT_ID(N'commerce.TourMedia')
              AND index_id = @lookupIndexId
              AND type = 2 AND is_unique = 0 AND is_primary_key = 0
              AND is_unique_constraint = 0 AND is_disabled = 0 AND has_filter = 0)
       OR INDEX_COL(N'commerce.TourMedia', @lookupIndexId, 1) <> N'tour_id'
       OR INDEX_COL(N'commerce.TourMedia', @lookupIndexId, 2) <> N'lifecycle_status'
       OR INDEX_COL(N'commerce.TourMedia', @lookupIndexId, 3) <> N'sort_order'
       OR INDEX_COL(N'commerce.TourMedia', @lookupIndexId, 4) <> N'tour_media_id'
       OR INDEX_COL(N'commerce.TourMedia', @lookupIndexId, 5) IS NOT NULL
       OR EXISTS (SELECT 1 FROM sys.index_columns WHERE object_id = OBJECT_ID(N'commerce.TourMedia')
                  AND index_id = @lookupIndexId AND is_included_column = 1)
        THROW 51000, 'TM-206 TourMedia gallery lookup index shape mismatch.', 1;

    COMMIT TRANSACTION;
END TRY
BEGIN CATCH
    IF XACT_STATE() <> 0 ROLLBACK TRANSACTION;
    THROW;
END CATCH;
GO
