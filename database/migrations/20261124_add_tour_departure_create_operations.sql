/*
    Add durable idempotency table for adding new departure schedules to approved/published tours.
    Additive and idempotent; validates existing schema shape atomically on re-runs.
*/
SET XACT_ABORT ON;
GO

BEGIN TRY
    BEGIN TRANSACTION;

    IF OBJECT_ID(N'dbo.Users', N'U') IS NULL
        THROW 51000, 'Migration requires dbo.Users.', 1;
    IF OBJECT_ID(N'commerce.Tours', N'U') IS NULL
        THROW 51000, 'Migration requires commerce.Tours.', 1;
    IF OBJECT_ID(N'commerce.TourSchedules', N'U') IS NULL
        THROW 51000, 'Migration requires commerce.TourSchedules.', 1;

    DECLARE @databaseCollation SYSNAME = CONVERT(
        SYSNAME, DATABASEPROPERTYEX(DB_NAME(), N'Collation'));

    IF OBJECT_ID(N'commerce.TourDepartureCreateOperations', N'U') IS NULL
    BEGIN
        IF OBJECT_ID(N'commerce.TourDepartureCreateOperations') IS NOT NULL
            THROW 51000, 'commerce.TourDepartureCreateOperations exists with wrong object type.', 1;

        CREATE TABLE commerce.TourDepartureCreateOperations (
            create_operation_id BIGINT IDENTITY(1,1) NOT NULL
                CONSTRAINT PK_TourDepartureCreateOperations PRIMARY KEY,
            actor_user_id BIGINT NOT NULL,
            tour_id BIGINT NOT NULL,
            idempotency_key UNIQUEIDENTIFIER NOT NULL,
            payload_fingerprint CHAR(64) COLLATE Latin1_General_100_BIN2 NOT NULL,
            schedule_id BIGINT NULL,
            created_at DATETIME2 NOT NULL
                CONSTRAINT DF_TourDepartureCreateOperations_CreatedAt DEFAULT SYSUTCDATETIME(),
            completed_at DATETIME2 NULL,
            CONSTRAINT FK_TourDepartureCreateOperations_Actor FOREIGN KEY (actor_user_id)
                REFERENCES dbo.Users(user_id),
            CONSTRAINT FK_TourDepartureCreateOperations_Tour FOREIGN KEY (tour_id)
                REFERENCES commerce.Tours(tour_id) ON DELETE CASCADE,
            CONSTRAINT FK_TourDepartureCreateOperations_Schedule FOREIGN KEY (schedule_id)
                REFERENCES commerce.TourSchedules(schedule_id),
            CONSTRAINT CK_TourDepartureCreateOperations_Fingerprint CHECK (
                LEN(payload_fingerprint) = 64
                AND payload_fingerprint NOT LIKE '%[^0-9A-F]%'
                    COLLATE Latin1_General_100_BIN2),
            CONSTRAINT UQ_TourDepartureCreateOperations_ActorTourKey
                UNIQUE (actor_user_id, tour_id, idempotency_key)
        );
    END;

    IF (SELECT COUNT(*) FROM sys.columns
        WHERE object_id = OBJECT_ID(N'commerce.TourDepartureCreateOperations')) <> 8
        THROW 51000, 'TourDepartureCreateOperations column count mismatch.', 1;

    IF EXISTS (
        SELECT expected.name
        FROM (VALUES
            (N'create_operation_id', N'bigint', 8, 19, 0, 0, 1,
                CAST(NULL AS SYSNAME), CAST(NULL AS SYSNAME), CAST(NULL AS NVARCHAR(128))),
            (N'actor_user_id', N'bigint', 8, 19, 0, 0, 0, NULL, NULL, NULL),
            (N'tour_id', N'bigint', 8, 19, 0, 0, 0, NULL, NULL, NULL),
            (N'idempotency_key', N'uniqueidentifier', 16, 0, 0, 0, 0, NULL, NULL, NULL),
            (N'payload_fingerprint', N'char', 64, 0, 0, 0, 0,
                N'Latin1_General_100_BIN2', NULL, NULL),
            (N'schedule_id', N'bigint', 8, 19, 0, 1, 0, NULL, NULL, NULL),
            (N'created_at', N'datetime2', 8, 27, 7, 0, 0,
                NULL, N'DF_TourDepartureCreateOperations_CreatedAt', N'sysutcdatetime'),
            (N'completed_at', N'datetime2', 8, 27, 7, 1, 0, NULL, NULL, NULL)
        ) AS expected(
            name, type_name, max_length, precision_value, scale_value,
            is_nullable, is_identity, collation_name,
            default_name, default_definition)
        LEFT JOIN sys.columns AS actual
          ON actual.object_id = OBJECT_ID(N'commerce.TourDepartureCreateOperations')
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
        THROW 51000, 'TourDepartureCreateOperations column/default shape mismatch.', 1;

    IF NOT EXISTS (
        SELECT 1 FROM sys.key_constraints
        WHERE parent_object_id = OBJECT_ID(N'commerce.TourDepartureCreateOperations')
          AND name = N'PK_TourDepartureCreateOperations' AND type = N'PK')
        THROW 51000, 'TourDepartureCreateOperations primary key mismatch.', 1;

    IF NOT EXISTS (
        SELECT 1 FROM sys.key_constraints
        WHERE parent_object_id = OBJECT_ID(N'commerce.TourDepartureCreateOperations')
          AND name = N'UQ_TourDepartureCreateOperations_ActorTourKey' AND type = N'UQ')
        THROW 51000, 'TourDepartureCreateOperations unique constraint mismatch.', 1;

    IF NOT EXISTS (
        SELECT 1 FROM sys.foreign_keys
        WHERE parent_object_id = OBJECT_ID(N'commerce.TourDepartureCreateOperations')
          AND name = N'FK_TourDepartureCreateOperations_Actor'
          AND referenced_object_id = OBJECT_ID(N'dbo.Users')
          AND delete_referential_action = 0
          AND is_disabled = 0 AND is_not_trusted = 0)
       OR NOT EXISTS (
        SELECT 1 FROM sys.foreign_keys
        WHERE parent_object_id = OBJECT_ID(N'commerce.TourDepartureCreateOperations')
          AND name = N'FK_TourDepartureCreateOperations_Tour'
          AND referenced_object_id = OBJECT_ID(N'commerce.Tours')
          AND delete_referential_action = 1
          AND is_disabled = 0 AND is_not_trusted = 0)
       OR NOT EXISTS (
        SELECT 1 FROM sys.foreign_keys
        WHERE parent_object_id = OBJECT_ID(N'commerce.TourDepartureCreateOperations')
          AND name = N'FK_TourDepartureCreateOperations_Schedule'
          AND referenced_object_id = OBJECT_ID(N'commerce.TourSchedules')
          AND delete_referential_action = 0
          AND is_disabled = 0 AND is_not_trusted = 0)
        THROW 51000, 'TourDepartureCreateOperations foreign key mismatch.', 1;

    IF OBJECT_ID(N'commerce.CK_TourDepartureCreateOperations_Fingerprint', N'C') IS NULL
        THROW 51000, 'TourDepartureCreateOperations check constraint mismatch.', 1;

    COMMIT TRANSACTION;
END TRY
BEGIN CATCH
    IF XACT_STATE() <> 0 ROLLBACK TRANSACTION;
    THROW;
END CATCH;
GO
