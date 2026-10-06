SET XACT_ABORT ON;
SET NOCOUNT ON;

BEGIN TRY
    BEGIN TRANSACTION;

    IF OBJECT_ID(N'planning.SchedulingRequests', N'U') IS NULL
        THROW 51002, 'planning.SchedulingRequests must exist before applying the generation reservation migration.', 1;

    IF COL_LENGTH(N'planning.SchedulingRequests', N'generation_owner_id') IS NULL
        ALTER TABLE planning.SchedulingRequests
            ADD generation_owner_id UNIQUEIDENTIFIER NULL;

    IF COL_LENGTH(N'planning.SchedulingRequests', N'generation_lease_expires_at') IS NULL
        ALTER TABLE planning.SchedulingRequests
            ADD generation_lease_expires_at DATETIME2 NULL;

    IF COL_LENGTH(N'planning.SchedulingRequests', N'generation_attempt') IS NULL
        ALTER TABLE planning.SchedulingRequests
            ADD generation_attempt INT NOT NULL
                CONSTRAINT DF_SchedulingRequests_GenerationAttempt DEFAULT 0;

    -- Older code never persisted lease ownership. A legacy Processing row has no
    -- safe owner to preserve, so make it immediately claimable.
    -- Dynamic SQL gives SQL Server a new compilation boundary after the columns
    -- above are added. Without it, a legacy schema fails batch compilation before
    -- the ALTER statements can run.
    EXEC sys.sp_executesql N'
        UPDATE planning.SchedulingRequests
        SET status = ''Pending'',
            completed_at = NULL,
            failure_code = NULL,
            failure_message = NULL,
            generation_owner_id = NULL,
            generation_lease_expires_at = NULL
        WHERE status = ''Processing''
          AND (generation_owner_id IS NULL OR generation_lease_expires_at IS NULL);';

    IF NOT EXISTS (
        SELECT 1
        FROM sys.default_constraints AS defaults
        INNER JOIN sys.columns AS columns
            ON columns.object_id = defaults.parent_object_id
           AND columns.column_id = defaults.parent_column_id
        WHERE defaults.parent_object_id = OBJECT_ID(N'planning.SchedulingRequests')
          AND columns.name = N'generation_attempt')
        ALTER TABLE planning.SchedulingRequests
            ADD CONSTRAINT DF_SchedulingRequests_GenerationAttempt
            DEFAULT 0 FOR generation_attempt;

    IF EXISTS (
        SELECT 1
        FROM sys.check_constraints
        WHERE parent_object_id = OBJECT_ID(N'planning.SchedulingRequests')
          AND name = N'CK_SchedulingRequests_GenerationReservation')
    BEGIN
        DECLARE @reservationDefinition NVARCHAR(MAX) = LOWER(REPLACE(REPLACE(
            OBJECT_DEFINITION(OBJECT_ID(N'planning.CK_SchedulingRequests_GenerationReservation', N'C')),
            N'[', N''), N']', N''));
        SET @reservationDefinition = REPLACE(REPLACE(REPLACE(
            @reservationDefinition, N' ', N''), CHAR(13), N''), CHAR(10), N'');
        SET @reservationDefinition = REPLACE(@reservationDefinition, N'(0)', N'0');

        IF @reservationDefinition
            <> N'(status=''processing''andgeneration_owner_idisnotnullandgeneration_lease_expires_atisnotnullandgeneration_attempt>0orstatus<>''processing''andgeneration_owner_idisnullandgeneration_lease_expires_atisnullandgeneration_attempt>=0)'
           OR EXISTS (
                SELECT 1
                FROM sys.check_constraints
                WHERE parent_object_id = OBJECT_ID(N'planning.SchedulingRequests')
                  AND name = N'CK_SchedulingRequests_GenerationReservation'
                  AND (is_disabled = 1 OR is_not_trusted = 1))
            THROW 51002, 'Scheduling generation reservation schema contract mismatch: check constraint.', 1;
    END;
    ELSE
    BEGIN
        EXEC sys.sp_executesql N'
            ALTER TABLE planning.SchedulingRequests WITH CHECK
                ADD CONSTRAINT CK_SchedulingRequests_GenerationReservation CHECK (
                    (status = ''Processing''
                        AND generation_owner_id IS NOT NULL
                        AND generation_lease_expires_at IS NOT NULL
                        AND generation_attempt > 0)
                    OR (status <> ''Processing''
                        AND generation_owner_id IS NULL
                        AND generation_lease_expires_at IS NULL
                        AND generation_attempt >= 0));';
    END;

    IF EXISTS (
        SELECT 1
        FROM (VALUES
            (N'generation_owner_id', N'uniqueidentifier', CAST(16 AS SMALLINT), CAST(NULL AS TINYINT), CAST(NULL AS TINYINT), CAST(1 AS BIT)),
            (N'generation_lease_expires_at', N'datetime2', CAST(8 AS SMALLINT), CAST(27 AS TINYINT), CAST(7 AS TINYINT), CAST(1 AS BIT)),
            (N'generation_attempt', N'int', CAST(4 AS SMALLINT), CAST(10 AS TINYINT), CAST(0 AS TINYINT), CAST(0 AS BIT))
        ) AS expected(column_name, type_name, max_length, precision_value, scale_value, is_nullable)
        LEFT JOIN sys.columns AS columns
            ON columns.object_id = OBJECT_ID(N'planning.SchedulingRequests')
           AND columns.name = expected.column_name
        LEFT JOIN sys.types AS types
            ON types.user_type_id = columns.user_type_id
        WHERE columns.column_id IS NULL
           OR types.name <> expected.type_name
           OR columns.max_length <> expected.max_length
           OR ISNULL(columns.precision, 0) <> ISNULL(expected.precision_value, 0)
           OR ISNULL(columns.scale, 0) <> ISNULL(expected.scale_value, 0)
           OR columns.is_nullable <> expected.is_nullable)
        THROW 51002, 'Scheduling generation reservation schema contract mismatch: columns.', 1;

    IF NOT EXISTS (
        SELECT 1
        FROM sys.default_constraints AS defaults
        INNER JOIN sys.columns AS columns
            ON columns.object_id = defaults.parent_object_id
           AND columns.column_id = defaults.parent_column_id
        WHERE defaults.parent_object_id = OBJECT_ID(N'planning.SchedulingRequests')
          AND defaults.name = N'DF_SchedulingRequests_GenerationAttempt'
          AND columns.name = N'generation_attempt'
          AND REPLACE(REPLACE(defaults.definition, N'(', N''), N')', N'') = N'0')
        THROW 51002, 'Scheduling generation reservation schema contract mismatch: generation attempt default.', 1;

    COMMIT TRANSACTION;
END TRY
BEGIN CATCH
    IF XACT_STATE() <> 0
        ROLLBACK TRANSACTION;
    THROW;
END CATCH;
