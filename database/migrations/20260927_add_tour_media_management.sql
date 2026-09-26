/*
    TM-207: Operator Tour Media management persistence.
    Additive and idempotent. SQL Server stores metadata/workflow state only;
    image binaries and Cloudinary credentials are never stored here.
*/
SET XACT_ABORT ON;
GO

BEGIN TRY
    BEGIN TRANSACTION;

    IF OBJECT_ID(N'dbo.Users', N'U') IS NULL
        THROW 51000, 'TM-207 migration requires dbo.Users.', 1;
    IF OBJECT_ID(N'commerce.Tours', N'U') IS NULL
        THROW 51000, 'TM-207 migration requires commerce.Tours.', 1;
    IF OBJECT_ID(N'commerce.TourMedia', N'U') IS NULL
        THROW 51000, 'TM-207 migration requires commerce.TourMedia from TM-206.', 1;

    DECLARE @databaseCollation SYSNAME = CONVERT(
        SYSNAME, DATABASEPROPERTYEX(DB_NAME(), N'Collation'));

    /* -----------------------------------------------------------------
       TourMedia additions
       ----------------------------------------------------------------- */
    IF COL_LENGTH(N'commerce.TourMedia', N'alt_text') IS NULL
    BEGIN
        EXEC(N'ALTER TABLE commerce.TourMedia
            ADD alt_text NVARCHAR(500) COLLATE Vietnamese_100_CI_AS NULL;');

        EXEC(N'UPDATE commerce.TourMedia
            SET alt_text = N''Tour image''
            WHERE alt_text IS NULL;');

        EXEC(N'ALTER TABLE commerce.TourMedia
            ALTER COLUMN alt_text NVARCHAR(500)
                COLLATE Vietnamese_100_CI_AS NOT NULL;');
    END;

    IF NOT EXISTS (
        SELECT 1
        FROM sys.columns AS column_info
        JOIN sys.types AS type_info
          ON type_info.user_type_id = column_info.user_type_id
        WHERE column_info.object_id = OBJECT_ID(N'commerce.TourMedia')
          AND column_info.name = N'alt_text'
          AND type_info.name = N'nvarchar'
          AND column_info.max_length = 1000
          AND column_info.precision = 0
          AND column_info.scale = 0
          AND column_info.is_nullable = 0
          AND column_info.is_identity = 0
          AND column_info.is_computed = 0
          AND column_info.collation_name = N'Vietnamese_100_CI_AS')
        THROW 51000, 'TM-207 TourMedia.alt_text shape mismatch; manual reconciliation is required.', 1;

    IF EXISTS (
        SELECT cloudinary_public_id
        FROM commerce.TourMedia
        GROUP BY cloudinary_public_id
        HAVING COUNT(*) > 1)
        THROW 51000, 'TM-207 cannot enforce provider-id uniqueness while duplicate TourMedia values exist.', 1;

    IF NOT EXISTS (
        SELECT 1 FROM sys.indexes
        WHERE object_id = OBJECT_ID(N'commerce.TourMedia')
          AND name = N'UX_TourMedia_CloudinaryPublicId')
    BEGIN
        CREATE UNIQUE INDEX UX_TourMedia_CloudinaryPublicId
            ON commerce.TourMedia(cloudinary_public_id);
    END;

    DECLARE @publicIdIndexId INT = INDEXPROPERTY(
        OBJECT_ID(N'commerce.TourMedia'),
        N'UX_TourMedia_CloudinaryPublicId', N'IndexId');
    IF @publicIdIndexId IS NULL
       OR NOT EXISTS (
            SELECT 1 FROM sys.indexes
            WHERE object_id = OBJECT_ID(N'commerce.TourMedia')
              AND index_id = @publicIdIndexId
              AND type = 2 AND is_unique = 1 AND is_primary_key = 0
              AND is_unique_constraint = 0 AND is_disabled = 0
              AND has_filter = 0)
       OR INDEX_COL(N'commerce.TourMedia', @publicIdIndexId, 1)
            <> N'cloudinary_public_id'
       OR INDEX_COL(N'commerce.TourMedia', @publicIdIndexId, 2) IS NOT NULL
       OR EXISTS (
            SELECT 1 FROM sys.index_columns
            WHERE object_id = OBJECT_ID(N'commerce.TourMedia')
              AND index_id = @publicIdIndexId
              AND is_included_column = 1)
        THROW 51000, 'TM-207 TourMedia provider-id index shape mismatch.', 1;

    /* -----------------------------------------------------------------
       Upload idempotency operations
       ----------------------------------------------------------------- */
    IF OBJECT_ID(N'commerce.TourMediaUploadOperations', N'U') IS NULL
    BEGIN
        IF OBJECT_ID(N'commerce.TourMediaUploadOperations') IS NOT NULL
            THROW 51000, 'TM-207 upload-operation object has the wrong type.', 1;

        CREATE TABLE commerce.TourMediaUploadOperations (
            upload_operation_id BIGINT IDENTITY(1,1) NOT NULL
                CONSTRAINT PK_TourMediaUploadOperations PRIMARY KEY,
            actor_user_id BIGINT NOT NULL,
            tour_id BIGINT NOT NULL,
            idempotency_key UNIQUEIDENTIFIER NOT NULL,
            payload_fingerprint CHAR(64) COLLATE Latin1_General_100_BIN2 NOT NULL,
            cloudinary_public_id NVARCHAR(500) NOT NULL,
            operation_status VARCHAR(16) NOT NULL
                CONSTRAINT DF_TourMediaUploadOperations_Status DEFAULT 'Pending',
            tour_media_id BIGINT NULL,
            provider_uploaded_at DATETIME2 NULL,
            completed_at DATETIME2 NULL,
            created_at DATETIME2 NOT NULL
                CONSTRAINT DF_TourMediaUploadOperations_CreatedAt DEFAULT SYSUTCDATETIME(),
            updated_at DATETIME2 NOT NULL
                CONSTRAINT DF_TourMediaUploadOperations_UpdatedAt DEFAULT SYSUTCDATETIME(),
            CONSTRAINT FK_TourMediaUploadOperations_Actor FOREIGN KEY (actor_user_id)
                REFERENCES dbo.Users(user_id),
            CONSTRAINT FK_TourMediaUploadOperations_Tour FOREIGN KEY (tour_id)
                REFERENCES commerce.Tours(tour_id) ON DELETE CASCADE,
            CONSTRAINT CK_TourMediaUploadOperations_Fingerprint CHECK (
                LEN(payload_fingerprint) = 64
                AND payload_fingerprint NOT LIKE '%[^0-9A-F]%'
                    COLLATE Latin1_General_100_BIN2),
            CONSTRAINT CK_TourMediaUploadOperations_Status CHECK (
                operation_status IN ('Pending','Uploaded','Completed')),
            CONSTRAINT CK_TourMediaUploadOperations_State CHECK (
                (operation_status = 'Pending'
                    AND provider_uploaded_at IS NULL
                    AND tour_media_id IS NULL
                    AND completed_at IS NULL)
                OR (operation_status = 'Uploaded'
                    AND provider_uploaded_at IS NOT NULL
                    AND tour_media_id IS NULL
                    AND completed_at IS NULL)
                OR (operation_status = 'Completed'
                    AND provider_uploaded_at IS NOT NULL
                    AND tour_media_id IS NOT NULL
                    AND completed_at IS NOT NULL))
        );

        CREATE UNIQUE INDEX UX_TourMediaUploadOperations_ActorTourKey
            ON commerce.TourMediaUploadOperations(
                actor_user_id, tour_id, idempotency_key);
        CREATE UNIQUE INDEX UX_TourMediaUploadOperations_PublicId
            ON commerce.TourMediaUploadOperations(cloudinary_public_id);
        CREATE INDEX IX_TourMediaUploadOperations_TourStatus
            ON commerce.TourMediaUploadOperations(
                tour_id, operation_status, upload_operation_id);
    END;

    IF (SELECT COUNT(*) FROM sys.columns
        WHERE object_id = OBJECT_ID(N'commerce.TourMediaUploadOperations')) <> 12
        THROW 51000, 'TM-207 upload-operation column count mismatch.', 1;

    IF EXISTS (
        SELECT expected.name
        FROM (VALUES
            (N'upload_operation_id', N'bigint', 8, 19, 0, 0, 1,
                CAST(NULL AS SYSNAME), CAST(NULL AS SYSNAME), CAST(NULL AS NVARCHAR(128))),
            (N'actor_user_id', N'bigint', 8, 19, 0, 0, 0, NULL, NULL, NULL),
            (N'tour_id', N'bigint', 8, 19, 0, 0, 0, NULL, NULL, NULL),
            (N'idempotency_key', N'uniqueidentifier', 16, 0, 0, 0, 0, NULL, NULL, NULL),
            (N'payload_fingerprint', N'char', 64, 0, 0, 0, 0,
                N'Latin1_General_100_BIN2', NULL, NULL),
            (N'cloudinary_public_id', N'nvarchar', 1000, 0, 0, 0, 0,
                @databaseCollation, NULL, NULL),
            (N'operation_status', N'varchar', 16, 0, 0, 0, 0,
                @databaseCollation, N'DF_TourMediaUploadOperations_Status', N'''pending'''),
            (N'tour_media_id', N'bigint', 8, 19, 0, 1, 0, NULL, NULL, NULL),
            (N'provider_uploaded_at', N'datetime2', 8, 27, 7, 1, 0, NULL, NULL, NULL),
            (N'completed_at', N'datetime2', 8, 27, 7, 1, 0, NULL, NULL, NULL),
            (N'created_at', N'datetime2', 8, 27, 7, 0, 0,
                NULL, N'DF_TourMediaUploadOperations_CreatedAt', N'sysutcdatetime'),
            (N'updated_at', N'datetime2', 8, 27, 7, 0, 0,
                NULL, N'DF_TourMediaUploadOperations_UpdatedAt', N'sysutcdatetime')
        ) AS expected(
            name, type_name, max_length, precision_value, scale_value,
            is_nullable, is_identity, collation_name,
            default_name, default_definition)
        LEFT JOIN sys.columns AS actual
          ON actual.object_id = OBJECT_ID(N'commerce.TourMediaUploadOperations')
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
           OR actual.is_computed <> 0
           OR ISNULL(actual.collation_name, N'<null>')
                <> ISNULL(expected.collation_name, N'<null>')
           OR ISNULL(default_constraint.name, N'<null>')
                <> ISNULL(expected.default_name, N'<null>')
           OR ISNULL(LOWER(REPLACE(REPLACE(REPLACE(
                default_constraint.definition, N'(', N''), N')', N''), N' ', N'')), N'<null>')
                <> ISNULL(expected.default_definition, N'<null>'))
        THROW 51000, 'TM-207 upload-operation column/default shape mismatch.', 1;

    IF (SELECT COUNT(*) FROM sys.key_constraints
        WHERE parent_object_id = OBJECT_ID(N'commerce.TourMediaUploadOperations')) <> 1
       OR NOT EXISTS (
            SELECT 1 FROM sys.key_constraints
            WHERE parent_object_id = OBJECT_ID(N'commerce.TourMediaUploadOperations')
              AND name = N'PK_TourMediaUploadOperations' AND type = N'PK')
        THROW 51000, 'TM-207 upload-operation primary key mismatch.', 1;

    IF (SELECT COUNT(*) FROM sys.foreign_keys
        WHERE parent_object_id = OBJECT_ID(N'commerce.TourMediaUploadOperations')) <> 2
       OR NOT EXISTS (
            SELECT 1 FROM sys.foreign_keys
            WHERE parent_object_id = OBJECT_ID(N'commerce.TourMediaUploadOperations')
              AND name = N'FK_TourMediaUploadOperations_Actor'
              AND referenced_object_id = OBJECT_ID(N'dbo.Users')
              AND delete_referential_action = 0
              AND is_disabled = 0 AND is_not_trusted = 0)
       OR NOT EXISTS (
            SELECT 1 FROM sys.foreign_keys
            WHERE parent_object_id = OBJECT_ID(N'commerce.TourMediaUploadOperations')
              AND name = N'FK_TourMediaUploadOperations_Tour'
              AND referenced_object_id = OBJECT_ID(N'commerce.Tours')
              AND delete_referential_action = 1
              AND is_disabled = 0 AND is_not_trusted = 0)
        THROW 51000, 'TM-207 upload-operation foreign key mismatch.', 1;

    IF (SELECT COUNT(*) FROM sys.check_constraints
        WHERE parent_object_id = OBJECT_ID(N'commerce.TourMediaUploadOperations')) <> 3
       OR EXISTS (
            SELECT 1 FROM sys.check_constraints
            WHERE parent_object_id = OBJECT_ID(N'commerce.TourMediaUploadOperations')
              AND (is_disabled = 1 OR is_not_trusted = 1))
       OR OBJECT_ID(N'commerce.CK_TourMediaUploadOperations_Fingerprint', N'C') IS NULL
       OR OBJECT_ID(N'commerce.CK_TourMediaUploadOperations_Status', N'C') IS NULL
       OR OBJECT_ID(N'commerce.CK_TourMediaUploadOperations_State', N'C') IS NULL
        THROW 51000, 'TM-207 upload-operation check inventory mismatch.', 1;

    DECLARE @uploadStatusCheck NVARCHAR(MAX) = LOWER(REPLACE(REPLACE(REPLACE(REPLACE(
        OBJECT_DEFINITION(OBJECT_ID(N'commerce.CK_TourMediaUploadOperations_Status', N'C')),
        N'[', N''), N']', N''), N'(', N''), N')', N''));
    SET @uploadStatusCheck = REPLACE(REPLACE(REPLACE(
        @uploadStatusCheck, N' ', N''), CHAR(13), N''), CHAR(10), N'');
    IF @uploadStatusCheck IS NULL
       OR @uploadStatusCheck NOT LIKE N'%operation_status=''pending''%'
       OR @uploadStatusCheck NOT LIKE N'%operation_status=''uploaded''%'
       OR @uploadStatusCheck NOT LIKE N'%operation_status=''completed''%'
        THROW 51000, 'TM-207 upload-operation status check mismatch.', 1;

    DECLARE @uploadFingerprintCheck NVARCHAR(MAX) = LOWER(REPLACE(REPLACE(REPLACE(REPLACE(
        OBJECT_DEFINITION(OBJECT_ID(N'commerce.CK_TourMediaUploadOperations_Fingerprint', N'C')),
        N'[', N''), N']', N''), N'(', N''), N')', N''));
    SET @uploadFingerprintCheck = REPLACE(REPLACE(REPLACE(
        @uploadFingerprintCheck, N' ', N''), CHAR(13), N''), CHAR(10), N'');
    IF @uploadFingerprintCheck IS NULL
        THROW 51000, 'TM-207 upload-operation fingerprint check mismatch.', 1;

    DECLARE @uploadStateCheck NVARCHAR(MAX) = LOWER(REPLACE(REPLACE(REPLACE(REPLACE(
        OBJECT_DEFINITION(OBJECT_ID(N'commerce.CK_TourMediaUploadOperations_State', N'C')),
        N'[', N''), N']', N''), N'(', N''), N')', N''));
    SET @uploadStateCheck = REPLACE(REPLACE(REPLACE(
        @uploadStateCheck, N' ', N''), CHAR(13), N''), CHAR(10), N'');
    IF @uploadStateCheck IS NULL
       OR @uploadStateCheck NOT LIKE N'%operation_status=''pending''andprovider_uploaded_atisnullandtour_media_idisnullandcompleted_atisnull%'
       OR @uploadStateCheck NOT LIKE N'%operation_status=''uploaded''andprovider_uploaded_atisnotnullandtour_media_idisnullandcompleted_atisnull%'
       OR @uploadStateCheck NOT LIKE N'%operation_status=''completed''andprovider_uploaded_atisnotnullandtour_media_idisnotnullandcompleted_atisnotnull%'
        THROW 51000, 'TM-207 upload-operation state check mismatch.', 1;

    DECLARE @uploadKeyIndexId INT = INDEXPROPERTY(
        OBJECT_ID(N'commerce.TourMediaUploadOperations'),
        N'UX_TourMediaUploadOperations_ActorTourKey', N'IndexId');
    DECLARE @uploadPublicIdIndexId INT = INDEXPROPERTY(
        OBJECT_ID(N'commerce.TourMediaUploadOperations'),
        N'UX_TourMediaUploadOperations_PublicId', N'IndexId');
    DECLARE @uploadLookupIndexId INT = INDEXPROPERTY(
        OBJECT_ID(N'commerce.TourMediaUploadOperations'),
        N'IX_TourMediaUploadOperations_TourStatus', N'IndexId');

    IF (SELECT COUNT(*) FROM sys.indexes
        WHERE object_id = OBJECT_ID(N'commerce.TourMediaUploadOperations')
          AND index_id > 0 AND is_hypothetical = 0) <> 4
       OR @uploadKeyIndexId IS NULL
       OR INDEX_COL(N'commerce.TourMediaUploadOperations', @uploadKeyIndexId, 1) <> N'actor_user_id'
       OR INDEX_COL(N'commerce.TourMediaUploadOperations', @uploadKeyIndexId, 2) <> N'tour_id'
       OR INDEX_COL(N'commerce.TourMediaUploadOperations', @uploadKeyIndexId, 3) <> N'idempotency_key'
       OR INDEX_COL(N'commerce.TourMediaUploadOperations', @uploadKeyIndexId, 4) IS NOT NULL
       OR @uploadPublicIdIndexId IS NULL
       OR INDEX_COL(N'commerce.TourMediaUploadOperations', @uploadPublicIdIndexId, 1) <> N'cloudinary_public_id'
       OR INDEX_COL(N'commerce.TourMediaUploadOperations', @uploadPublicIdIndexId, 2) IS NOT NULL
       OR @uploadLookupIndexId IS NULL
       OR INDEX_COL(N'commerce.TourMediaUploadOperations', @uploadLookupIndexId, 1) <> N'tour_id'
       OR INDEX_COL(N'commerce.TourMediaUploadOperations', @uploadLookupIndexId, 2) <> N'operation_status'
       OR INDEX_COL(N'commerce.TourMediaUploadOperations', @uploadLookupIndexId, 3) <> N'upload_operation_id'
       OR INDEX_COL(N'commerce.TourMediaUploadOperations', @uploadLookupIndexId, 4) IS NOT NULL
       OR EXISTS (
            SELECT 1 FROM sys.indexes
            WHERE object_id = OBJECT_ID(N'commerce.TourMediaUploadOperations')
              AND index_id IN (@uploadKeyIndexId, @uploadPublicIdIndexId)
              AND (is_unique = 0 OR is_disabled = 1 OR has_filter = 1))
       OR EXISTS (
            SELECT 1 FROM sys.indexes
            WHERE object_id = OBJECT_ID(N'commerce.TourMediaUploadOperations')
              AND index_id = @uploadLookupIndexId
              AND (is_unique = 1 OR is_disabled = 1 OR has_filter = 1))
        THROW 51000, 'TM-207 upload-operation index inventory mismatch.', 1;

    /* -----------------------------------------------------------------
       Delayed provider cleanup outbox
       ----------------------------------------------------------------- */
    IF OBJECT_ID(N'commerce.TourMediaCleanupOutbox', N'U') IS NULL
    BEGIN
        IF OBJECT_ID(N'commerce.TourMediaCleanupOutbox') IS NOT NULL
            THROW 51000, 'TM-207 cleanup-outbox object has the wrong type.', 1;

        CREATE TABLE commerce.TourMediaCleanupOutbox (
            cleanup_outbox_id BIGINT IDENTITY(1,1) NOT NULL
                CONSTRAINT PK_TourMediaCleanupOutbox PRIMARY KEY,
            tour_media_id BIGINT NULL,
            cloudinary_public_id NVARCHAR(500) NOT NULL,
            cleanup_status VARCHAR(16) NOT NULL
                CONSTRAINT DF_TourMediaCleanupOutbox_Status DEFAULT 'Pending',
            not_before_at DATETIME2 NOT NULL,
            attempt_count INT NOT NULL
                CONSTRAINT DF_TourMediaCleanupOutbox_AttemptCount DEFAULT 0,
            max_attempts INT NOT NULL
                CONSTRAINT DF_TourMediaCleanupOutbox_MaxAttempts DEFAULT 8,
            lease_token UNIQUEIDENTIFIER NULL,
            lease_expires_at DATETIME2 NULL,
            last_error_code NVARCHAR(100) NULL,
            created_at DATETIME2 NOT NULL
                CONSTRAINT DF_TourMediaCleanupOutbox_CreatedAt DEFAULT SYSUTCDATETIME(),
            updated_at DATETIME2 NOT NULL
                CONSTRAINT DF_TourMediaCleanupOutbox_UpdatedAt DEFAULT SYSUTCDATETIME(),
            completed_at DATETIME2 NULL,
            CONSTRAINT FK_TourMediaCleanupOutbox_TourMedia FOREIGN KEY (tour_media_id)
                REFERENCES commerce.TourMedia(tour_media_id) ON DELETE SET NULL,
            CONSTRAINT CK_TourMediaCleanupOutbox_Status CHECK (
                cleanup_status IN ('Pending','InProgress','Completed','Exhausted')),
            CONSTRAINT CK_TourMediaCleanupOutbox_Attempts CHECK (
                max_attempts BETWEEN 1 AND 100
                AND attempt_count BETWEEN 0 AND max_attempts),
            CONSTRAINT CK_TourMediaCleanupOutbox_State CHECK (
                (cleanup_status = 'Pending'
                    AND lease_token IS NULL
                    AND lease_expires_at IS NULL
                    AND completed_at IS NULL)
                OR (cleanup_status = 'InProgress'
                    AND lease_token IS NOT NULL
                    AND lease_expires_at IS NOT NULL
                    AND completed_at IS NULL)
                OR (cleanup_status = 'Completed'
                    AND lease_token IS NULL
                    AND lease_expires_at IS NULL
                    AND completed_at IS NOT NULL)
                OR (cleanup_status = 'Exhausted'
                    AND lease_token IS NULL
                    AND lease_expires_at IS NULL
                    AND completed_at IS NOT NULL
                    AND attempt_count = max_attempts))
        );

        CREATE UNIQUE INDEX UX_TourMediaCleanupOutbox_PublicId
            ON commerce.TourMediaCleanupOutbox(cloudinary_public_id);
        CREATE UNIQUE INDEX UX_TourMediaCleanupOutbox_Media
            ON commerce.TourMediaCleanupOutbox(tour_media_id)
            WHERE tour_media_id IS NOT NULL;
        CREATE INDEX IX_TourMediaCleanupOutbox_Due
            ON commerce.TourMediaCleanupOutbox(
                cleanup_status, not_before_at, lease_expires_at, cleanup_outbox_id);
    END;

    IF (SELECT COUNT(*) FROM sys.columns
        WHERE object_id = OBJECT_ID(N'commerce.TourMediaCleanupOutbox')) <> 13
        THROW 51000, 'TM-207 cleanup-outbox column count mismatch.', 1;

    IF EXISTS (
        SELECT expected.name
        FROM (VALUES
            (N'cleanup_outbox_id', N'bigint', 8, 19, 0, 0, 1,
                CAST(NULL AS SYSNAME), CAST(NULL AS SYSNAME), CAST(NULL AS NVARCHAR(128))),
            (N'tour_media_id', N'bigint', 8, 19, 0, 1, 0, NULL, NULL, NULL),
            (N'cloudinary_public_id', N'nvarchar', 1000, 0, 0, 0, 0,
                @databaseCollation, NULL, NULL),
            (N'cleanup_status', N'varchar', 16, 0, 0, 0, 0,
                @databaseCollation, N'DF_TourMediaCleanupOutbox_Status', N'''pending'''),
            (N'not_before_at', N'datetime2', 8, 27, 7, 0, 0, NULL, NULL, NULL),
            (N'attempt_count', N'int', 4, 10, 0, 0, 0,
                NULL, N'DF_TourMediaCleanupOutbox_AttemptCount', N'0'),
            (N'max_attempts', N'int', 4, 10, 0, 0, 0,
                NULL, N'DF_TourMediaCleanupOutbox_MaxAttempts', N'8'),
            (N'lease_token', N'uniqueidentifier', 16, 0, 0, 1, 0, NULL, NULL, NULL),
            (N'lease_expires_at', N'datetime2', 8, 27, 7, 1, 0, NULL, NULL, NULL),
            (N'last_error_code', N'nvarchar', 200, 0, 0, 1, 0,
                @databaseCollation, NULL, NULL),
            (N'created_at', N'datetime2', 8, 27, 7, 0, 0,
                NULL, N'DF_TourMediaCleanupOutbox_CreatedAt', N'sysutcdatetime'),
            (N'updated_at', N'datetime2', 8, 27, 7, 0, 0,
                NULL, N'DF_TourMediaCleanupOutbox_UpdatedAt', N'sysutcdatetime'),
            (N'completed_at', N'datetime2', 8, 27, 7, 1, 0, NULL, NULL, NULL)
        ) AS expected(
            name, type_name, max_length, precision_value, scale_value,
            is_nullable, is_identity, collation_name,
            default_name, default_definition)
        LEFT JOIN sys.columns AS actual
          ON actual.object_id = OBJECT_ID(N'commerce.TourMediaCleanupOutbox')
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
           OR actual.is_computed <> 0
           OR ISNULL(actual.collation_name, N'<null>')
                <> ISNULL(expected.collation_name, N'<null>')
           OR ISNULL(default_constraint.name, N'<null>')
                <> ISNULL(expected.default_name, N'<null>')
           OR ISNULL(LOWER(REPLACE(REPLACE(REPLACE(
                default_constraint.definition, N'(', N''), N')', N''), N' ', N'')), N'<null>')
                <> ISNULL(expected.default_definition, N'<null>'))
        THROW 51000, 'TM-207 cleanup-outbox column/default shape mismatch.', 1;

    IF (SELECT COUNT(*) FROM sys.key_constraints
        WHERE parent_object_id = OBJECT_ID(N'commerce.TourMediaCleanupOutbox')) <> 1
       OR NOT EXISTS (
            SELECT 1 FROM sys.key_constraints
            WHERE parent_object_id = OBJECT_ID(N'commerce.TourMediaCleanupOutbox')
              AND name = N'PK_TourMediaCleanupOutbox' AND type = N'PK')
        THROW 51000, 'TM-207 cleanup-outbox primary key mismatch.', 1;

    IF (SELECT COUNT(*) FROM sys.foreign_keys
        WHERE parent_object_id = OBJECT_ID(N'commerce.TourMediaCleanupOutbox')) <> 1
       OR NOT EXISTS (
            SELECT 1
            FROM sys.foreign_keys AS foreign_key
            JOIN sys.foreign_key_columns AS column_map
              ON column_map.constraint_object_id = foreign_key.object_id
            WHERE foreign_key.parent_object_id = OBJECT_ID(N'commerce.TourMediaCleanupOutbox')
              AND foreign_key.name = N'FK_TourMediaCleanupOutbox_TourMedia'
              AND foreign_key.referenced_object_id = OBJECT_ID(N'commerce.TourMedia')
              AND foreign_key.delete_referential_action = 2
              AND foreign_key.update_referential_action = 0
              AND foreign_key.is_disabled = 0
              AND foreign_key.is_not_trusted = 0
              AND column_map.constraint_column_id = 1
              AND COL_NAME(column_map.parent_object_id, column_map.parent_column_id) = N'tour_media_id'
              AND COL_NAME(column_map.referenced_object_id, column_map.referenced_column_id) = N'tour_media_id')
        THROW 51000, 'TM-207 cleanup-outbox foreign key mismatch.', 1;

    IF (SELECT COUNT(*) FROM sys.check_constraints
        WHERE parent_object_id = OBJECT_ID(N'commerce.TourMediaCleanupOutbox')) <> 3
       OR EXISTS (
            SELECT 1 FROM sys.check_constraints
            WHERE parent_object_id = OBJECT_ID(N'commerce.TourMediaCleanupOutbox')
              AND (is_disabled = 1 OR is_not_trusted = 1))
       OR OBJECT_ID(N'commerce.CK_TourMediaCleanupOutbox_Status', N'C') IS NULL
       OR OBJECT_ID(N'commerce.CK_TourMediaCleanupOutbox_Attempts', N'C') IS NULL
       OR OBJECT_ID(N'commerce.CK_TourMediaCleanupOutbox_State', N'C') IS NULL
        THROW 51000, 'TM-207 cleanup-outbox check inventory mismatch.', 1;

    DECLARE @cleanupStatusCheck NVARCHAR(MAX) = LOWER(REPLACE(REPLACE(REPLACE(REPLACE(
        OBJECT_DEFINITION(OBJECT_ID(N'commerce.CK_TourMediaCleanupOutbox_Status', N'C')),
        N'[', N''), N']', N''), N'(', N''), N')', N''));
    SET @cleanupStatusCheck = REPLACE(REPLACE(REPLACE(
        @cleanupStatusCheck, N' ', N''), CHAR(13), N''), CHAR(10), N'');
    IF @cleanupStatusCheck IS NULL
       OR @cleanupStatusCheck NOT LIKE N'%cleanup_status=''pending''%'
       OR @cleanupStatusCheck NOT LIKE N'%cleanup_status=''inprogress''%'
       OR @cleanupStatusCheck NOT LIKE N'%cleanup_status=''completed''%'
       OR @cleanupStatusCheck NOT LIKE N'%cleanup_status=''exhausted''%'
        THROW 51000, 'TM-207 cleanup-outbox status check mismatch.', 1;

    DECLARE @cleanupAttemptsCheck NVARCHAR(MAX) = LOWER(REPLACE(REPLACE(REPLACE(REPLACE(
        OBJECT_DEFINITION(OBJECT_ID(N'commerce.CK_TourMediaCleanupOutbox_Attempts', N'C')),
        N'[', N''), N']', N''), N'(', N''), N')', N''));
    SET @cleanupAttemptsCheck = REPLACE(REPLACE(REPLACE(
        @cleanupAttemptsCheck, N' ', N''), CHAR(13), N''), CHAR(10), N'');
    IF @cleanupAttemptsCheck IS NULL
       OR @cleanupAttemptsCheck NOT LIKE N'%max_attempts>=1%max_attempts<=100%'
       OR @cleanupAttemptsCheck NOT LIKE N'%attempt_count>=0%attempt_count<=max_attempts%'
        THROW 51000, 'TM-207 cleanup-outbox attempts check mismatch.', 1;

    DECLARE @cleanupStateCheck NVARCHAR(MAX) = LOWER(REPLACE(REPLACE(REPLACE(REPLACE(
        OBJECT_DEFINITION(OBJECT_ID(N'commerce.CK_TourMediaCleanupOutbox_State', N'C')),
        N'[', N''), N']', N''), N'(', N''), N')', N''));
    SET @cleanupStateCheck = REPLACE(REPLACE(REPLACE(
        @cleanupStateCheck, N' ', N''), CHAR(13), N''), CHAR(10), N'');
    IF @cleanupStateCheck IS NULL
       OR @cleanupStateCheck NOT LIKE N'%cleanup_status=''pending''andlease_tokenisnullandlease_expires_atisnullandcompleted_atisnull%'
       OR @cleanupStateCheck NOT LIKE N'%cleanup_status=''inprogress''andlease_tokenisnotnullandlease_expires_atisnotnullandcompleted_atisnull%'
       OR @cleanupStateCheck NOT LIKE N'%cleanup_status=''completed''andlease_tokenisnullandlease_expires_atisnullandcompleted_atisnotnull%'
       OR @cleanupStateCheck NOT LIKE N'%cleanup_status=''exhausted''andlease_tokenisnullandlease_expires_atisnullandcompleted_atisnotnullandattempt_count=max_attempts%'
        THROW 51000, 'TM-207 cleanup-outbox state check mismatch.', 1;

    DECLARE @cleanupPublicIdIndexId INT = INDEXPROPERTY(
        OBJECT_ID(N'commerce.TourMediaCleanupOutbox'),
        N'UX_TourMediaCleanupOutbox_PublicId', N'IndexId');
    DECLARE @cleanupMediaIndexId INT = INDEXPROPERTY(
        OBJECT_ID(N'commerce.TourMediaCleanupOutbox'),
        N'UX_TourMediaCleanupOutbox_Media', N'IndexId');
    DECLARE @cleanupDueIndexId INT = INDEXPROPERTY(
        OBJECT_ID(N'commerce.TourMediaCleanupOutbox'),
        N'IX_TourMediaCleanupOutbox_Due', N'IndexId');

    IF (SELECT COUNT(*) FROM sys.indexes
        WHERE object_id = OBJECT_ID(N'commerce.TourMediaCleanupOutbox')
          AND index_id > 0 AND is_hypothetical = 0) <> 4
       OR @cleanupPublicIdIndexId IS NULL
       OR INDEX_COL(N'commerce.TourMediaCleanupOutbox', @cleanupPublicIdIndexId, 1) <> N'cloudinary_public_id'
       OR INDEX_COL(N'commerce.TourMediaCleanupOutbox', @cleanupPublicIdIndexId, 2) IS NOT NULL
       OR @cleanupMediaIndexId IS NULL
       OR INDEX_COL(N'commerce.TourMediaCleanupOutbox', @cleanupMediaIndexId, 1) <> N'tour_media_id'
       OR INDEX_COL(N'commerce.TourMediaCleanupOutbox', @cleanupMediaIndexId, 2) IS NOT NULL
       OR @cleanupDueIndexId IS NULL
       OR INDEX_COL(N'commerce.TourMediaCleanupOutbox', @cleanupDueIndexId, 1) <> N'cleanup_status'
       OR INDEX_COL(N'commerce.TourMediaCleanupOutbox', @cleanupDueIndexId, 2) <> N'not_before_at'
       OR INDEX_COL(N'commerce.TourMediaCleanupOutbox', @cleanupDueIndexId, 3) <> N'lease_expires_at'
       OR INDEX_COL(N'commerce.TourMediaCleanupOutbox', @cleanupDueIndexId, 4) <> N'cleanup_outbox_id'
       OR INDEX_COL(N'commerce.TourMediaCleanupOutbox', @cleanupDueIndexId, 5) IS NOT NULL
       OR EXISTS (
            SELECT 1 FROM sys.indexes
            WHERE object_id = OBJECT_ID(N'commerce.TourMediaCleanupOutbox')
              AND index_id = @cleanupPublicIdIndexId
              AND (is_unique = 0 OR is_disabled = 1 OR has_filter = 1))
       OR NOT EXISTS (
            SELECT 1 FROM sys.indexes
            WHERE object_id = OBJECT_ID(N'commerce.TourMediaCleanupOutbox')
              AND index_id = @cleanupMediaIndexId
              AND is_unique = 1 AND is_disabled = 0 AND has_filter = 1
              AND LOWER(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(
                    filter_definition,
                    N'[', N''), N']', N''), N'(', N''), N')', N''), N' ', N''))
                    = N'tour_media_idisnotnull')
       OR EXISTS (
            SELECT 1 FROM sys.indexes
            WHERE object_id = OBJECT_ID(N'commerce.TourMediaCleanupOutbox')
              AND index_id = @cleanupDueIndexId
              AND (is_unique = 1 OR is_disabled = 1 OR has_filter = 1))
        THROW 51000, 'TM-207 cleanup-outbox index inventory mismatch.', 1;

    COMMIT TRANSACTION;
END TRY
BEGIN CATCH
    IF XACT_STATE() <> 0 ROLLBACK TRANSACTION;
    THROW;
END CATCH;
GO
