SET ANSI_NULLS ON;
SET ANSI_PADDING ON;
SET ANSI_WARNINGS ON;
SET ARITHABORT ON;
SET CONCAT_NULL_YIELDS_NULL ON;
SET QUOTED_IDENTIFIER ON;
SET NUMERIC_ROUNDABORT OFF;
SET XACT_ABORT ON;
SET NOCOUNT ON;

BEGIN TRY
    BEGIN TRANSACTION;

    IF OBJECT_ID(N'trip.TripSessions', N'U') IS NULL
        THROW 51013, 'trip.TripSessions must exist before applying the UC-13 navigation migration.', 1;

    IF OBJECT_ID(N'trip.TripStateHistory', N'U') IS NULL
        THROW 51013, 'trip.TripStateHistory must exist before applying the UC-13 navigation migration.', 1;

    IF OBJECT_ID(N'planning.Itineraries', N'U') IS NULL
       OR OBJECT_ID(N'planning.ItineraryItems', N'U') IS NULL
        THROW 51013, 'The planning itinerary schema must exist before applying the UC-13 navigation migration.', 1;

    IF EXISTS (
        SELECT 1
        FROM (VALUES
            (N'session_id', N'bigint', CAST(8 AS SMALLINT), CAST(0 AS BIT)),
            (N'from_state', N'varchar', CAST(12 AS SMALLINT), CAST(1 AS BIT)),
            (N'to_state', N'varchar', CAST(12 AS SMALLINT), CAST(0 AS BIT)),
            (N'reason', N'nvarchar', CAST(1000 AS SMALLINT), CAST(1 AS BIT)),
            (N'triggered_by', N'varchar', CAST(20 AS SMALLINT), CAST(1 AS BIT)),
            (N'changed_at', N'datetime2', CAST(8 AS SMALLINT), CAST(0 AS BIT))
        ) AS expected(column_name, type_name, max_length, is_nullable)
        LEFT JOIN sys.columns AS columns
            ON columns.object_id = OBJECT_ID(N'trip.TripStateHistory')
           AND columns.name = expected.column_name
        LEFT JOIN sys.types AS types
            ON types.user_type_id = columns.user_type_id
        WHERE columns.column_id IS NULL
           OR types.name <> expected.type_name
           OR columns.max_length <> expected.max_length
           OR columns.is_nullable <> expected.is_nullable)
        THROW 51013, 'trip.TripStateHistory has an incompatible column contract.', 1;

    IF NOT EXISTS (
        SELECT 1
        FROM sys.check_constraints
        WHERE parent_object_id = OBJECT_ID(N'trip.TripStateHistory')
          AND definition LIKE N'%System%'
          AND definition LIKE N'%Traveler%')
        THROW 51013, 'trip.TripStateHistory must allow the System and Traveler triggers.', 1;

    IF EXISTS (
        SELECT 1
        FROM trip.TripSessions
        WHERE ended_at IS NULL
        GROUP BY traveler_user_id
        HAVING COUNT_BIG(*) > 1)
        THROW 51013, 'UC-13 migration blocked: a Traveler has multiple open trip sessions.', 1;

    IF COL_LENGTH(N'trip.TripSessions', N'requested_itinerary_id') IS NULL
        EXEC sys.sp_executesql N'
            ALTER TABLE trip.TripSessions
            ADD requested_itinerary_id BIGINT NULL;';

    IF COL_LENGTH(N'trip.TripSessions', N'start_idempotency_key') IS NULL
        EXEC sys.sp_executesql N'
            ALTER TABLE trip.TripSessions
            ADD start_idempotency_key UNIQUEIDENTIFIER NULL;';

    IF COL_LENGTH(N'trip.TripSessions', N'completion_reason') IS NULL
        EXEC sys.sp_executesql N'
            ALTER TABLE trip.TripSessions
            ADD completion_reason VARCHAR(24) NULL;';

    IF COL_LENGTH(N'trip.TripSessions', N'row_version') IS NULL
        EXEC sys.sp_executesql N'
            ALTER TABLE trip.TripSessions
            ADD row_version ROWVERSION NOT NULL;';

    IF EXISTS (
        SELECT 1
        FROM (VALUES
            (N'requested_itinerary_id', CAST(127 AS TINYINT), CAST(8 AS SMALLINT)),
            (N'start_idempotency_key', CAST(36 AS TINYINT), CAST(16 AS SMALLINT)),
            (N'completion_reason', CAST(167 AS TINYINT), CAST(24 AS SMALLINT)),
            (N'row_version', CAST(189 AS TINYINT), CAST(8 AS SMALLINT))
        ) AS expected(column_name, system_type_id, max_length)
        LEFT JOIN sys.columns AS columns
            ON columns.object_id = OBJECT_ID(N'trip.TripSessions')
           AND columns.name = expected.column_name
        WHERE columns.column_id IS NULL
           OR columns.system_type_id <> expected.system_type_id
           OR columns.max_length <> expected.max_length)
        THROW 51013, 'trip.TripSessions has an incompatible UC-13 column contract.', 1;

    EXEC sys.sp_executesql N'
        UPDATE trip.TripSessions
        SET requested_itinerary_id = itinerary_id
        WHERE requested_itinerary_id IS NULL;

        UPDATE trip.TripSessions
        SET start_idempotency_key = NEWID()
        WHERE start_idempotency_key IS NULL;';

    IF EXISTS (
        SELECT 1
        FROM sys.columns
        WHERE object_id = OBJECT_ID(N'trip.TripSessions')
          AND name = N'requested_itinerary_id'
          AND is_nullable = 1)
        EXEC sys.sp_executesql N'
            ALTER TABLE trip.TripSessions
            ALTER COLUMN requested_itinerary_id BIGINT NOT NULL;';

    IF EXISTS (
        SELECT 1
        FROM sys.columns
        WHERE object_id = OBJECT_ID(N'trip.TripSessions')
          AND name = N'start_idempotency_key'
          AND is_nullable = 1)
        EXEC sys.sp_executesql N'
            ALTER TABLE trip.TripSessions
            ALTER COLUMN start_idempotency_key UNIQUEIDENTIFIER NOT NULL;';

    IF NOT EXISTS (
        SELECT 1 FROM sys.foreign_keys
        WHERE parent_object_id = OBJECT_ID(N'trip.TripSessions')
          AND name = N'FK_TripSessions_RequestedItinerary')
        EXEC sys.sp_executesql N'
            ALTER TABLE trip.TripSessions WITH CHECK
                ADD CONSTRAINT FK_TripSessions_RequestedItinerary
                FOREIGN KEY (requested_itinerary_id)
                REFERENCES planning.Itineraries(itinerary_id);';

    IF NOT EXISTS (
        SELECT 1 FROM sys.check_constraints
        WHERE parent_object_id = OBJECT_ID(N'trip.TripSessions')
          AND name = N'CK_TripSessions_CompletionReason')
        EXEC sys.sp_executesql N'
            ALTER TABLE trip.TripSessions WITH CHECK
                ADD CONSTRAINT CK_TripSessions_CompletionReason CHECK (
                    completion_reason IS NULL
                    OR completion_reason IN (''AllItemsReached'', ''TravelerStopped''));';

    IF EXISTS (
        SELECT 1
        FROM sys.columns
        WHERE object_id = OBJECT_ID(N'trip.TripSessions')
          AND name IN (N'requested_itinerary_id', N'start_idempotency_key', N'row_version')
          AND is_nullable = 1)
        THROW 51013, 'Required UC-13 TripSessions columns must be NOT NULL.', 1;

    IF EXISTS (
        SELECT 1
        FROM sys.foreign_keys
        WHERE parent_object_id = OBJECT_ID(N'trip.TripSessions')
          AND name = N'FK_TripSessions_RequestedItinerary'
          AND (referenced_object_id <> OBJECT_ID(N'planning.Itineraries')
               OR is_disabled = 1
               OR is_not_trusted = 1))
        THROW 51013, 'FK_TripSessions_RequestedItinerary has an incompatible definition.', 1;

    IF EXISTS (
        SELECT 1
        FROM sys.check_constraints
        WHERE parent_object_id = OBJECT_ID(N'trip.TripSessions')
          AND name = N'CK_TripSessions_CompletionReason'
          AND (is_disabled = 1
               OR is_not_trusted = 1
               OR definition NOT LIKE N'%AllItemsReached%'
               OR definition NOT LIKE N'%TravelerStopped%'))
        THROW 51013, 'CK_TripSessions_CompletionReason must be enabled and trusted.', 1;

    IF NOT EXISTS (
        SELECT 1 FROM sys.indexes
        WHERE object_id = OBJECT_ID(N'trip.TripSessions')
          AND name = N'UQ_TripSessions_Traveler_StartKey')
        EXEC sys.sp_executesql N'
            CREATE UNIQUE INDEX UQ_TripSessions_Traveler_StartKey
                ON trip.TripSessions(traveler_user_id, start_idempotency_key);';

    IF NOT EXISTS (
        SELECT 1 FROM sys.indexes
        WHERE object_id = OBJECT_ID(N'trip.TripSessions')
          AND name = N'UQ_TripSessions_OpenTraveler')
        CREATE UNIQUE INDEX UQ_TripSessions_OpenTraveler
            ON trip.TripSessions(traveler_user_id)
            WHERE ended_at IS NULL;

    IF EXISTS (
        SELECT 1
        FROM sys.indexes AS indexes
        WHERE indexes.object_id = OBJECT_ID(N'trip.TripSessions')
          AND indexes.name = N'UQ_TripSessions_Traveler_StartKey'
          AND (indexes.is_unique = 0
               OR indexes.has_filter = 1
               OR 2 <> (
                    SELECT COUNT(*)
                    FROM sys.index_columns AS columns
                    WHERE columns.object_id = indexes.object_id
                      AND columns.index_id = indexes.index_id
                      AND columns.key_ordinal > 0)
               OR NOT EXISTS (
                    SELECT 1
                    FROM sys.index_columns AS columns
                    INNER JOIN sys.columns AS table_columns
                        ON table_columns.object_id = columns.object_id
                       AND table_columns.column_id = columns.column_id
                    WHERE columns.object_id = indexes.object_id
                      AND columns.index_id = indexes.index_id
                      AND columns.key_ordinal = 1
                      AND table_columns.name = N'traveler_user_id')
               OR NOT EXISTS (
                    SELECT 1
                    FROM sys.index_columns AS columns
                    INNER JOIN sys.columns AS table_columns
                        ON table_columns.object_id = columns.object_id
                       AND table_columns.column_id = columns.column_id
                    WHERE columns.object_id = indexes.object_id
                      AND columns.index_id = indexes.index_id
                      AND columns.key_ordinal = 2
                      AND table_columns.name = N'start_idempotency_key')))
        THROW 51013, 'UQ_TripSessions_Traveler_StartKey has an incompatible definition.', 1;

    IF EXISTS (
        SELECT 1
        FROM sys.indexes AS indexes
        WHERE indexes.object_id = OBJECT_ID(N'trip.TripSessions')
          AND indexes.name = N'UQ_TripSessions_OpenTraveler'
          AND (indexes.is_unique = 0
               OR indexes.has_filter = 0
               OR indexes.filter_definition NOT LIKE N'%ended_at%IS NULL%'
               OR 1 <> (
                    SELECT COUNT(*)
                    FROM sys.index_columns AS columns
                    WHERE columns.object_id = indexes.object_id
                      AND columns.index_id = indexes.index_id
                      AND columns.key_ordinal > 0)
               OR NOT EXISTS (
                    SELECT 1
                    FROM sys.index_columns AS columns
                    INNER JOIN sys.columns AS table_columns
                        ON table_columns.object_id = columns.object_id
                       AND table_columns.column_id = columns.column_id
                    WHERE columns.object_id = indexes.object_id
                      AND columns.index_id = indexes.index_id
                      AND columns.key_ordinal = 1
                      AND table_columns.name = N'traveler_user_id')))
        THROW 51013, 'UQ_TripSessions_OpenTraveler has an incompatible definition.', 1;

    IF OBJECT_ID(N'trip.TripSessionItems', N'U') IS NULL
    BEGIN
        CREATE TABLE trip.TripSessionItems (
            session_id BIGINT NOT NULL,
            itinerary_item_id BIGINT NOT NULL,
            sequence_no INT NOT NULL,
            poi_id BIGINT NOT NULL,
            poi_name NVARCHAR(200) NOT NULL,
            latitude DECIMAL(9,6) NOT NULL,
            longitude DECIMAL(9,6) NOT NULL,
            reached_at DATETIME2 NULL,
            CONSTRAINT PK_TripSessionItems
                PRIMARY KEY (session_id, itinerary_item_id),
            CONSTRAINT UQ_TripSessionItems_Sequence
                UNIQUE (session_id, sequence_no),
            CONSTRAINT FK_TripSessionItems_Session
                FOREIGN KEY (session_id)
                REFERENCES trip.TripSessions(session_id)
                ON DELETE CASCADE,
            CONSTRAINT FK_TripSessionItems_ItineraryItem
                FOREIGN KEY (itinerary_item_id)
                REFERENCES planning.ItineraryItems(item_id)
        );
    END;

    IF EXISTS (
        SELECT 1
        FROM (VALUES
            (N'session_id', N'bigint', CAST(8 AS SMALLINT), CAST(0 AS BIT)),
            (N'itinerary_item_id', N'bigint', CAST(8 AS SMALLINT), CAST(0 AS BIT)),
            (N'sequence_no', N'int', CAST(4 AS SMALLINT), CAST(0 AS BIT)),
            (N'poi_id', N'bigint', CAST(8 AS SMALLINT), CAST(0 AS BIT)),
            (N'poi_name', N'nvarchar', CAST(400 AS SMALLINT), CAST(0 AS BIT)),
            (N'latitude', N'decimal', CAST(5 AS SMALLINT), CAST(0 AS BIT)),
            (N'longitude', N'decimal', CAST(5 AS SMALLINT), CAST(0 AS BIT)),
            (N'reached_at', N'datetime2', CAST(8 AS SMALLINT), CAST(1 AS BIT))
        ) AS expected(column_name, type_name, max_length, is_nullable)
        LEFT JOIN sys.columns AS columns
            ON columns.object_id = OBJECT_ID(N'trip.TripSessionItems')
           AND columns.name = expected.column_name
        LEFT JOIN sys.types AS types
            ON types.user_type_id = columns.user_type_id
        WHERE columns.column_id IS NULL
           OR types.name <> expected.type_name
           OR columns.max_length <> expected.max_length
           OR columns.is_nullable <> expected.is_nullable)
        THROW 51013, 'trip.TripSessionItems has an incompatible column contract.', 1;

    IF EXISTS (
        SELECT 1
        FROM sys.columns
        WHERE object_id = OBJECT_ID(N'trip.TripSessionItems')
          AND name IN (N'latitude', N'longitude')
          AND (precision <> 9 OR scale <> 6))
        THROW 51013, 'trip.TripSessionItems coordinates must be DECIMAL(9,6).', 1;

    IF NOT EXISTS (
        SELECT 1 FROM sys.indexes
        WHERE object_id = OBJECT_ID(N'trip.TripSessionItems')
          AND name = N'IX_TripSessionItems_Next')
        CREATE INDEX IX_TripSessionItems_Next
            ON trip.TripSessionItems(session_id, reached_at, sequence_no);

    IF NOT EXISTS (
        SELECT 1
        FROM sys.key_constraints
        WHERE parent_object_id = OBJECT_ID(N'trip.TripSessionItems')
          AND type = N'PK')
        THROW 51013, 'trip.TripSessionItems must have a primary key.', 1;

    IF NOT EXISTS (
        SELECT 1
        FROM sys.indexes
        WHERE object_id = OBJECT_ID(N'trip.TripSessionItems')
          AND name = N'UQ_TripSessionItems_Sequence'
          AND is_unique = 1)
        THROW 51013, 'UQ_TripSessionItems_Sequence is missing or incompatible.', 1;

    IF NOT EXISTS (
        SELECT 1
        FROM sys.foreign_keys
        WHERE parent_object_id = OBJECT_ID(N'trip.TripSessionItems')
          AND name = N'FK_TripSessionItems_Session'
          AND delete_referential_action = 1)
        THROW 51013, 'FK_TripSessionItems_Session is missing or incompatible.', 1;

    IF NOT EXISTS (
        SELECT 1
        FROM sys.foreign_keys
        WHERE parent_object_id = OBJECT_ID(N'trip.TripSessionItems')
          AND name = N'FK_TripSessionItems_ItineraryItem')
        THROW 51013, 'FK_TripSessionItems_ItineraryItem is missing or incompatible.', 1;

    COMMIT TRANSACTION;
END TRY
BEGIN CATCH
    IF XACT_STATE() <> 0
        ROLLBACK TRANSACTION;
    THROW;
END CATCH;
