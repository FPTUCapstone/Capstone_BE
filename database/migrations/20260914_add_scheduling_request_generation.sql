SET XACT_ABORT ON;
SET NOCOUNT ON;

BEGIN TRY
    BEGIN TRANSACTION;

    DECLARE @expectedSchedulingDefaults TABLE (
        SchemaName SYSNAME NOT NULL,
        TableName SYSNAME NOT NULL,
        ColumnName SYSNAME NOT NULL,
        ConstraintName SYSNAME NOT NULL,
        ExpectedDefinition NVARCHAR(200) NOT NULL
    );

    INSERT INTO @expectedSchedulingDefaults
        (SchemaName, TableName, ColumnName, ConstraintName, ExpectedDefinition)
    VALUES
        (N'planning', N'SchedulingRequests', N'time_zone_id',
            N'DF_SchedulingRequests_TimeZoneId', N'''asia/ho_chi_minh'''),
        (N'planning', N'SchedulingRequests', N'rest_preference',
            N'DF_SchedulingRequests_RestPreference', N'''auto'''),
        (N'planning', N'SchedulingRequests', N'mandatory_poi_ids_json',
            N'DF_SchedulingRequests_MandatoryPoiIds', N'''[]'''),
        (N'planning', N'ItineraryItems', N'item_kind',
            N'DF_ItineraryItems_ItemKind', N'''visit''');

    IF EXISTS (
        SELECT 1
        FROM @expectedSchedulingDefaults AS expected
        INNER JOIN sys.default_constraints AS default_constraint
            ON default_constraint.name = expected.ConstraintName
        INNER JOIN sys.columns AS column_metadata
            ON column_metadata.object_id = default_constraint.parent_object_id
           AND column_metadata.column_id = default_constraint.parent_column_id
        WHERE default_constraint.parent_object_id = OBJECT_ID(
                  QUOTENAME(expected.SchemaName) + N'.' + QUOTENAME(expected.TableName))
          AND (
                column_metadata.name <> expected.ColumnName
                OR REPLACE(
                       LOWER(REPLACE(REPLACE(REPLACE(
                           default_constraint.definition,
                           N'(', N''), N')', N''), N' ', N'')),
                       N'n''', N'''')
                    <> expected.ExpectedDefinition
              ))
        THROW 51000, 'Scheduling schema contract mismatch: default constraint.', 1;

    -- A pre-existing named object is not proof that it enforces the UC-10
    -- contract. Fail safely rather than silently accepting a conflicting
    -- schema that would diverge from a fresh installation.
    IF EXISTS (
        SELECT 1
        FROM sys.check_constraints
        WHERE parent_object_id = OBJECT_ID(N'planning.SchedulingRequests')
          AND name = N'CK_SchedulingRequests_RestPreference')
    BEGIN
        DECLARE @restPreferenceDefinition NVARCHAR(MAX) = LOWER(REPLACE(REPLACE(REPLACE(REPLACE(
            OBJECT_DEFINITION(OBJECT_ID(N'planning.CK_SchedulingRequests_RestPreference', N'C')),
            N'[', N''), N']', N''), N'(', N''), N')', N''));
        SET @restPreferenceDefinition = REPLACE(REPLACE(REPLACE(
            @restPreferenceDefinition, N' ', N''), CHAR(13), N''), CHAR(10), N'');

        IF @restPreferenceDefinition
            <> N'rest_preference=''frequent''orrest_preference=''none''orrest_preference=''auto'''
           OR EXISTS (
                SELECT 1
                FROM sys.check_constraints
                WHERE parent_object_id = OBJECT_ID(N'planning.SchedulingRequests')
                  AND name = N'CK_SchedulingRequests_RestPreference'
                  AND (is_disabled = 1 OR is_not_trusted = 1))
            THROW 51000, 'Scheduling schema contract mismatch: rest preference constraint.', 1;
    END;

    IF EXISTS (
        SELECT 1
        FROM sys.indexes
        WHERE object_id = OBJECT_ID(N'planning.SchedulingRequests')
          AND name = N'UX_SchedulingRequests_Traveler_Key')
    BEGIN
        DECLARE @operationIndexId INT = INDEXPROPERTY(
            OBJECT_ID(N'planning.SchedulingRequests'),
            N'UX_SchedulingRequests_Traveler_Key',
            N'IndexId');

        IF @operationIndexId IS NULL
           OR NOT EXISTS (
                SELECT 1
                FROM sys.indexes
                WHERE object_id = OBJECT_ID(N'planning.SchedulingRequests')
                  AND index_id = @operationIndexId
                  AND type = 2
                  AND is_unique = 1
                  AND is_disabled = 0
                  AND has_filter = 0)
           OR INDEX_COL(N'planning.SchedulingRequests', @operationIndexId, 1)
              <> N'traveler_user_id'
           OR INDEX_COL(N'planning.SchedulingRequests', @operationIndexId, 2)
              <> N'idempotency_key'
           OR INDEX_COL(N'planning.SchedulingRequests', @operationIndexId, 3) IS NOT NULL
           OR EXISTS (
                SELECT 1
                FROM sys.index_columns
                WHERE object_id = OBJECT_ID(N'planning.SchedulingRequests')
                  AND index_id = @operationIndexId
                  AND is_included_column = 1)
            THROW 51000, 'Scheduling schema contract mismatch: idempotency index.', 1;
    END;

    IF EXISTS (
        SELECT 1
        FROM sys.check_constraints
        WHERE parent_object_id = OBJECT_ID(N'catalog.POIs')
          AND name = N'CK_POIs_EstimatedVisitCost_NonNegative')
    BEGIN
        DECLARE @estimatedVisitCostDefinition NVARCHAR(MAX) = LOWER(REPLACE(REPLACE(REPLACE(REPLACE(
            OBJECT_DEFINITION(OBJECT_ID(N'catalog.CK_POIs_EstimatedVisitCost_NonNegative', N'C')),
            N'[', N''), N']', N''), N'(', N''), N')', N''));
        SET @estimatedVisitCostDefinition = REPLACE(REPLACE(REPLACE(
            @estimatedVisitCostDefinition, N' ', N''), CHAR(13), N''), CHAR(10), N'');

        IF @estimatedVisitCostDefinition
            <> N'estimated_visit_costisnullorestimated_visit_cost>=0'
           OR EXISTS (
                SELECT 1
                FROM sys.check_constraints
                WHERE parent_object_id = OBJECT_ID(N'catalog.POIs')
                  AND name = N'CK_POIs_EstimatedVisitCost_NonNegative'
                  AND (is_disabled = 1 OR is_not_trusted = 1))
            THROW 51000, 'Scheduling schema contract mismatch: POI cost constraint.', 1;
    END;

    IF EXISTS (
        SELECT 1
        FROM sys.check_constraints
        WHERE parent_object_id = OBJECT_ID(N'planning.ItineraryItems')
          AND name = N'CK_ItineraryItems_KindPoi')
    BEGIN
        DECLARE @itineraryItemDefinition NVARCHAR(MAX) = LOWER(REPLACE(REPLACE(REPLACE(REPLACE(
            OBJECT_DEFINITION(OBJECT_ID(N'planning.CK_ItineraryItems_KindPoi', N'C')),
            N'[', N''), N']', N''), N'(', N''), N')', N''));
        SET @itineraryItemDefinition = REPLACE(REPLACE(REPLACE(
            @itineraryItemDefinition, N' ', N''), CHAR(13), N''), CHAR(10), N'');

        IF @itineraryItemDefinition
            <> N'item_kind=''visit''andpoi_idisnotnulloritem_kind=''rest''andis_mandatory=0'
           OR EXISTS (
                SELECT 1
                FROM sys.check_constraints
                WHERE parent_object_id = OBJECT_ID(N'planning.ItineraryItems')
                  AND name = N'CK_ItineraryItems_KindPoi'
                  AND (is_disabled = 1 OR is_not_trusted = 1))
            THROW 51000, 'Scheduling schema contract mismatch: itinerary item constraint.', 1;
    END;

    IF COL_LENGTH(N'planning.SchedulingRequests', N'start_at') IS NULL
    BEGIN
        ALTER TABLE planning.SchedulingRequests ADD start_at DATETIME2 NULL;
    END;

    IF EXISTS (
        SELECT 1
        FROM sys.columns
        WHERE object_id = OBJECT_ID(N'planning.SchedulingRequests')
          AND name = N'start_at'
          AND is_nullable = 1)
    BEGIN
        EXEC sys.sp_executesql N'
            UPDATE planning.SchedulingRequests
            SET start_at = requested_at
            WHERE start_at IS NULL;

            ALTER TABLE planning.SchedulingRequests ALTER COLUMN start_at DATETIME2 NOT NULL;';
    END;

    IF COL_LENGTH(N'planning.SchedulingRequests', N'time_zone_id') IS NULL
    BEGIN
        ALTER TABLE planning.SchedulingRequests
            ADD time_zone_id VARCHAR(100) NOT NULL
                CONSTRAINT DF_SchedulingRequests_TimeZoneId DEFAULT 'Asia/Ho_Chi_Minh';
    END;

    IF COL_LENGTH(N'planning.SchedulingRequests', N'idempotency_key') IS NULL
    BEGIN
        ALTER TABLE planning.SchedulingRequests ADD idempotency_key UNIQUEIDENTIFIER NULL;
    END;

    IF EXISTS (
        SELECT 1
        FROM sys.columns
        WHERE object_id = OBJECT_ID(N'planning.SchedulingRequests')
          AND name = N'idempotency_key'
          AND is_nullable = 1)
    BEGIN
        EXEC sys.sp_executesql N'
            UPDATE planning.SchedulingRequests
            SET idempotency_key = NEWID()
            WHERE idempotency_key IS NULL;

            ALTER TABLE planning.SchedulingRequests ALTER COLUMN idempotency_key UNIQUEIDENTIFIER NOT NULL;';
    END;

    IF COL_LENGTH(N'planning.SchedulingRequests', N'request_hash') IS NULL
    BEGIN
        ALTER TABLE planning.SchedulingRequests ADD request_hash CHAR(64) NULL;
    END;

    IF EXISTS (
        SELECT 1
        FROM sys.columns
        WHERE object_id = OBJECT_ID(N'planning.SchedulingRequests')
          AND name = N'request_hash'
          AND is_nullable = 1)
    BEGIN
        EXEC sys.sp_executesql N'
            UPDATE planning.SchedulingRequests
            SET request_hash = REPLICATE(''0'', 64)
            WHERE request_hash IS NULL;

            ALTER TABLE planning.SchedulingRequests ALTER COLUMN request_hash CHAR(64) NOT NULL;';
    END;

    IF COL_LENGTH(N'planning.SchedulingRequests', N'rest_preference') IS NULL
    BEGIN
        ALTER TABLE planning.SchedulingRequests
            ADD rest_preference VARCHAR(10) NOT NULL
                CONSTRAINT DF_SchedulingRequests_RestPreference DEFAULT 'Auto';
    END;

    IF COL_LENGTH(N'planning.SchedulingRequests', N'failure_code') IS NULL
    BEGIN
        ALTER TABLE planning.SchedulingRequests ADD failure_code VARCHAR(100) NULL;
    END;

    IF COL_LENGTH(N'planning.SchedulingRequests', N'failure_message') IS NULL
    BEGIN
        ALTER TABLE planning.SchedulingRequests ADD failure_message NVARCHAR(500) NULL;
    END;

    IF EXISTS (
        SELECT 1
        FROM sys.columns
        WHERE object_id = OBJECT_ID(N'planning.SchedulingRequests')
          AND name = N'destination_latitude'
          AND is_nullable = 1)
    BEGIN
        EXEC sys.sp_executesql N'
            UPDATE planning.SchedulingRequests
            SET destination_latitude = start_latitude
            WHERE destination_latitude IS NULL;

            ALTER TABLE planning.SchedulingRequests ALTER COLUMN destination_latitude DECIMAL(9,6) NOT NULL;';
    END;

    IF EXISTS (
        SELECT 1
        FROM sys.columns
        WHERE object_id = OBJECT_ID(N'planning.SchedulingRequests')
          AND name = N'destination_longitude'
          AND is_nullable = 1)
    BEGIN
        EXEC sys.sp_executesql N'
            UPDATE planning.SchedulingRequests
            SET destination_longitude = start_longitude
            WHERE destination_longitude IS NULL;

            ALTER TABLE planning.SchedulingRequests ALTER COLUMN destination_longitude DECIMAL(9,6) NOT NULL;';
    END;

    IF EXISTS (
        SELECT 1
        FROM sys.columns
        WHERE object_id = OBJECT_ID(N'planning.SchedulingRequests')
          AND name = N'search_radius_km'
          AND is_nullable = 1)
    BEGIN
        EXEC sys.sp_executesql N'
            UPDATE planning.SchedulingRequests
            SET search_radius_km = 10.00
            WHERE search_radius_km IS NULL;

            ALTER TABLE planning.SchedulingRequests ALTER COLUMN search_radius_km DECIMAL(6,2) NOT NULL;';
    END;

    IF EXISTS (
        SELECT 1
        FROM sys.columns
        WHERE object_id = OBJECT_ID(N'planning.SchedulingRequests')
          AND name = N'mandatory_poi_ids_json'
          AND is_nullable = 1)
    BEGIN
        EXEC sys.sp_executesql N'
            UPDATE planning.SchedulingRequests
            SET mandatory_poi_ids_json = N''[]''
            WHERE mandatory_poi_ids_json IS NULL;

            ALTER TABLE planning.SchedulingRequests ALTER COLUMN mandatory_poi_ids_json NVARCHAR(500) NOT NULL;';
    END;

    IF NOT EXISTS (
        SELECT 1
        FROM sys.default_constraints
        WHERE parent_object_id = OBJECT_ID(N'planning.SchedulingRequests')
          AND parent_column_id = COLUMNPROPERTY(
              OBJECT_ID(N'planning.SchedulingRequests'),
              N'mandatory_poi_ids_json',
              'ColumnId'))
    BEGIN
        ALTER TABLE planning.SchedulingRequests
            ADD CONSTRAINT DF_SchedulingRequests_MandatoryPoiIds
            DEFAULT (N'[]') FOR mandatory_poi_ids_json;
    END;

    EXEC sys.sp_executesql N'
        UPDATE planning.SchedulingRequests
        SET rest_preference = ''Auto''
        WHERE rest_preference NOT IN (''Auto'', ''None'', ''Frequent'')
           OR rest_preference IS NULL;

        IF NOT EXISTS (
            SELECT 1
            FROM sys.check_constraints
            WHERE parent_object_id = OBJECT_ID(N''planning.SchedulingRequests'')
              AND name = N''CK_SchedulingRequests_RestPreference'')
        BEGIN
            ALTER TABLE planning.SchedulingRequests
                ADD CONSTRAINT CK_SchedulingRequests_RestPreference
                CHECK (rest_preference IN (''Auto'', ''None'', ''Frequent''));
        END;';

    IF NOT EXISTS (
        SELECT 1
        FROM sys.indexes
        WHERE name = N'UX_SchedulingRequests_Traveler_Key'
          AND object_id = OBJECT_ID(N'planning.SchedulingRequests'))
    BEGIN
        CREATE UNIQUE INDEX UX_SchedulingRequests_Traveler_Key
            ON planning.SchedulingRequests(traveler_user_id, idempotency_key);
    END;

    IF COL_LENGTH(N'catalog.POIs', N'estimated_visit_cost') IS NULL
    BEGIN
        ALTER TABLE catalog.POIs ADD estimated_visit_cost DECIMAL(12, 2) NULL;
    END;

    EXEC sys.sp_executesql N'
        IF NOT EXISTS (
            SELECT 1
            FROM sys.check_constraints
            WHERE name = N''CK_POIs_EstimatedVisitCost_NonNegative''
              AND parent_object_id = OBJECT_ID(N''catalog.POIs''))
        BEGIN
            ALTER TABLE catalog.POIs
                ADD CONSTRAINT CK_POIs_EstimatedVisitCost_NonNegative
                CHECK (estimated_visit_cost IS NULL OR estimated_visit_cost >= 0);
        END;';

    IF COL_LENGTH(N'catalog.POIs', N'source_url') IS NULL
    BEGIN
        ALTER TABLE catalog.POIs ADD source_url NVARCHAR(500) NULL;
    END;

    IF COL_LENGTH(N'catalog.POIs', N'verified_at') IS NULL
    BEGIN
        ALTER TABLE catalog.POIs ADD verified_at DATETIME2 NULL;
    END;

    IF COL_LENGTH(N'planning.ItineraryItems', N'item_kind') IS NULL
    BEGIN
        ALTER TABLE planning.ItineraryItems ADD item_kind VARCHAR(10) NULL;
    END;

    IF EXISTS (
        SELECT 1
        FROM sys.columns
        WHERE object_id = OBJECT_ID(N'planning.ItineraryItems')
          AND name = N'item_kind'
          AND is_nullable = 1)
    BEGIN
        EXEC sys.sp_executesql N'
            UPDATE planning.ItineraryItems
            SET item_kind = ''Visit''
            WHERE item_kind IS NULL;

            ALTER TABLE planning.ItineraryItems ALTER COLUMN item_kind VARCHAR(10) NOT NULL;';

        IF NOT EXISTS (
            SELECT 1
            FROM sys.default_constraints
            WHERE parent_object_id = OBJECT_ID(N'planning.ItineraryItems')
              AND name = N'DF_ItineraryItems_ItemKind')
        BEGIN
            ALTER TABLE planning.ItineraryItems
                ADD CONSTRAINT DF_ItineraryItems_ItemKind DEFAULT 'Visit' FOR item_kind;
        END;
    END;

    IF EXISTS (
        SELECT 1
        FROM sys.columns
        WHERE object_id = OBJECT_ID(N'planning.ItineraryItems')
          AND name = N'poi_id'
          AND is_nullable = 0)
    BEGIN
        ALTER TABLE planning.ItineraryItems ALTER COLUMN poi_id BIGINT NULL;
    END;

    IF NOT EXISTS (
        SELECT 1
        FROM sys.check_constraints
        WHERE name = N'CK_ItineraryItems_KindPoi'
          AND parent_object_id = OBJECT_ID(N'planning.ItineraryItems'))
    BEGIN
        EXEC sys.sp_executesql N'
            ALTER TABLE planning.ItineraryItems
                ADD CONSTRAINT CK_ItineraryItems_KindPoi CHECK (
                    (item_kind = ''Visit'' AND poi_id IS NOT NULL)
                    OR (item_kind = ''Rest'' AND poi_id IS NULL AND is_mandatory = 0));';
    END;

    COMMIT TRANSACTION;
END TRY
BEGIN CATCH
    IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;
    THROW;
END CATCH;
GO
