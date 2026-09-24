SET XACT_ABORT ON;
SET NOCOUNT ON;

BEGIN TRY
    BEGIN TRANSACTION;

    IF EXISTS (
        SELECT 1
        FROM sys.check_constraints
        WHERE parent_object_id = OBJECT_ID(N'planning.SchedulingRequests')
          AND name = N'CK_SchedulingRequests_TransportMode')
    BEGIN
        DECLARE @transportModeDefinition NVARCHAR(MAX) = LOWER(REPLACE(REPLACE(REPLACE(REPLACE(
            OBJECT_DEFINITION(OBJECT_ID(N'planning.CK_SchedulingRequests_TransportMode', N'C')),
            N'[', N''), N']', N''), N'(', N''), N')', N''));
        SET @transportModeDefinition = REPLACE(REPLACE(REPLACE(
            @transportModeDefinition, N' ', N''), CHAR(13), N''), CHAR(10), N'');

        IF @transportModeDefinition
            <> N'transport_modein(''walking'',''motorbike'',''car'',''publictransit'')'
           OR EXISTS (
                SELECT 1
                FROM sys.check_constraints
                WHERE parent_object_id = OBJECT_ID(N'planning.SchedulingRequests')
                  AND name = N'CK_SchedulingRequests_TransportMode'
                  AND (is_disabled = 1 OR is_not_trusted = 1))
            THROW 51000, 'Scheduling schema contract mismatch: transport mode constraint.', 1;
    END;

    IF EXISTS (
        SELECT 1
        FROM sys.check_constraints
        WHERE parent_object_id = OBJECT_ID(N'planning.SchedulingRequests')
          AND name = N'CK_SchedulingRequests_EndChoice')
    BEGIN
        DECLARE @endChoiceDefinition NVARCHAR(MAX) = LOWER(REPLACE(REPLACE(REPLACE(REPLACE(
            OBJECT_DEFINITION(OBJECT_ID(N'planning.CK_SchedulingRequests_EndChoice', N'C')),
            N'[', N''), N']', N''), N'(', N''), N')', N''));
        SET @endChoiceDefinition = REPLACE(REPLACE(REPLACE(
            @endChoiceDefinition, N' ', N''), CHAR(13), N''), CHAR(10), N'');

        IF @endChoiceDefinition
            <> N'return_to_start=1andend_poi_idisnullorreturn_to_start=0andend_poi_idisnotnull'
           OR EXISTS (
                SELECT 1
                FROM sys.check_constraints
                WHERE parent_object_id = OBJECT_ID(N'planning.SchedulingRequests')
                  AND name = N'CK_SchedulingRequests_EndChoice'
                  AND (is_disabled = 1 OR is_not_trusted = 1))
            THROW 51000, 'Scheduling schema contract mismatch: end choice constraint.', 1;
    END;

    IF EXISTS (
        SELECT 1
        FROM sys.foreign_keys
        WHERE parent_object_id = OBJECT_ID(N'planning.SchedulingRequests')
          AND name = N'FK_SchedulingRequests_EndPoi')
       AND NOT EXISTS (
            SELECT 1
            FROM sys.foreign_keys AS foreign_key
            INNER JOIN sys.foreign_key_columns AS column_map
                ON column_map.constraint_object_id = foreign_key.object_id
            WHERE foreign_key.parent_object_id = OBJECT_ID(N'planning.SchedulingRequests')
              AND foreign_key.referenced_object_id = OBJECT_ID(N'catalog.POIs')
              AND foreign_key.name = N'FK_SchedulingRequests_EndPoi'
              AND foreign_key.delete_referential_action = 0
              AND foreign_key.update_referential_action = 0
              AND foreign_key.is_disabled = 0
              AND foreign_key.is_not_trusted = 0
              AND column_map.constraint_column_id = 1
              AND COL_NAME(column_map.parent_object_id, column_map.parent_column_id) = N'end_poi_id'
              AND COL_NAME(column_map.referenced_object_id, column_map.referenced_column_id) = N'poi_id')
        THROW 51000, 'Scheduling schema contract mismatch: ending POI foreign key.', 1;

    IF COL_LENGTH(N'planning.SchedulingRequests', N'end_poi_id') IS NULL
        ALTER TABLE planning.SchedulingRequests ADD end_poi_id BIGINT NULL;

    IF COL_LENGTH(N'planning.SchedulingRequests', N'return_to_start') IS NULL
        ALTER TABLE planning.SchedulingRequests ADD return_to_start BIT NOT NULL CONSTRAINT DF_SchedulingRequests_ReturnToStart DEFAULT 1;

    IF COL_LENGTH(N'planning.SchedulingRequests', N'transport_mode') IS NULL
        ALTER TABLE planning.SchedulingRequests ADD transport_mode VARCHAR(20) NOT NULL CONSTRAINT DF_SchedulingRequests_TransportMode DEFAULT 'Walking';

    EXEC sys.sp_executesql N'
        UPDATE planning.SchedulingRequests
        SET transport_mode = ''Walking''
        WHERE transport_mode NOT IN (''Walking'', ''Motorbike'', ''Car'', ''PublicTransit'')
           OR transport_mode IS NULL;

        IF NOT EXISTS (
            SELECT 1
            FROM sys.check_constraints
            WHERE parent_object_id = OBJECT_ID(N''planning.SchedulingRequests'')
              AND name = N''CK_SchedulingRequests_TransportMode'')
        BEGIN
            ALTER TABLE planning.SchedulingRequests
                ADD CONSTRAINT CK_SchedulingRequests_TransportMode
                CHECK (transport_mode IN (''Walking'', ''Motorbike'', ''Car'', ''PublicTransit''));
        END;';

    IF NOT EXISTS (
        SELECT 1 FROM sys.foreign_keys
        WHERE name = N'FK_SchedulingRequests_EndPoi'
          AND parent_object_id = OBJECT_ID(N'planning.SchedulingRequests'))
    BEGIN
        EXEC sys.sp_executesql N'
            ALTER TABLE planning.SchedulingRequests
                ADD CONSTRAINT FK_SchedulingRequests_EndPoi
                FOREIGN KEY (end_poi_id) REFERENCES catalog.POIs(poi_id);';
    END;

    IF NOT EXISTS (
        SELECT 1 FROM sys.check_constraints
        WHERE name = N'CK_SchedulingRequests_EndChoice'
          AND parent_object_id = OBJECT_ID(N'planning.SchedulingRequests'))
    BEGIN
        EXEC sys.sp_executesql N'
            ALTER TABLE planning.SchedulingRequests
                ADD CONSTRAINT CK_SchedulingRequests_EndChoice CHECK (
                    (return_to_start = 1 AND end_poi_id IS NULL)
                    OR (return_to_start = 0 AND end_poi_id IS NOT NULL));';
    END;

    DECLARE @expectedSchedulingColumns TABLE (
        SchemaName SYSNAME NOT NULL,
        TableName SYSNAME NOT NULL,
        ColumnName SYSNAME NOT NULL,
        TypeName SYSNAME NOT NULL,
        MaxLength SMALLINT NULL,
        PrecisionValue TINYINT NULL,
        ScaleValue TINYINT NULL,
        IsNullable BIT NOT NULL
    );

    INSERT INTO @expectedSchedulingColumns
        (SchemaName, TableName, ColumnName, TypeName, MaxLength,
         PrecisionValue, ScaleValue, IsNullable)
    VALUES
        (N'planning', N'SchedulingRequests', N'start_at', N'datetime2', NULL, NULL, 7, 0),
        (N'planning', N'SchedulingRequests', N'time_zone_id', N'varchar', 100, NULL, NULL, 0),
        (N'planning', N'SchedulingRequests', N'idempotency_key', N'uniqueidentifier', NULL, NULL, NULL, 0),
        (N'planning', N'SchedulingRequests', N'request_hash', N'char', 64, NULL, NULL, 0),
        (N'planning', N'SchedulingRequests', N'rest_preference', N'varchar', 10, NULL, NULL, 0),
        (N'planning', N'SchedulingRequests', N'failure_code', N'varchar', 100, NULL, NULL, 1),
        (N'planning', N'SchedulingRequests', N'failure_message', N'nvarchar', 1000, NULL, NULL, 1),
        (N'planning', N'SchedulingRequests', N'destination_latitude', N'decimal', NULL, 9, 6, 0),
        (N'planning', N'SchedulingRequests', N'destination_longitude', N'decimal', NULL, 9, 6, 0),
        (N'planning', N'SchedulingRequests', N'search_radius_km', N'decimal', NULL, 6, 2, 0),
        (N'planning', N'SchedulingRequests', N'mandatory_poi_ids_json', N'nvarchar', 1000, NULL, NULL, 0),
        (N'planning', N'SchedulingRequests', N'end_poi_id', N'bigint', NULL, NULL, NULL, 1),
        (N'planning', N'SchedulingRequests', N'return_to_start', N'bit', NULL, NULL, NULL, 0),
        (N'planning', N'SchedulingRequests', N'transport_mode', N'varchar', 20, NULL, NULL, 0),
        (N'catalog', N'POIs', N'estimated_visit_cost', N'decimal', NULL, 12, 2, 1),
        (N'catalog', N'POIs', N'source_url', N'nvarchar', 1000, NULL, NULL, 1),
        (N'catalog', N'POIs', N'verified_at', N'datetime2', NULL, NULL, 7, 1),
        (N'planning', N'ItineraryItems', N'item_kind', N'varchar', 10, NULL, NULL, 0),
        (N'planning', N'ItineraryItems', N'poi_id', N'bigint', NULL, NULL, NULL, 1);

    IF EXISTS (
        SELECT 1
        FROM @expectedSchedulingColumns AS expected
        INNER JOIN sys.columns AS column_metadata
            ON column_metadata.object_id = OBJECT_ID(
                   QUOTENAME(expected.SchemaName) + N'.' + QUOTENAME(expected.TableName))
           AND column_metadata.name = expected.ColumnName
        INNER JOIN sys.types AS type_metadata
            ON type_metadata.user_type_id = column_metadata.user_type_id
        WHERE LOWER(type_metadata.name) <> expected.TypeName
           OR (expected.MaxLength IS NOT NULL
               AND column_metadata.max_length <> expected.MaxLength)
           OR (expected.PrecisionValue IS NOT NULL
               AND column_metadata.precision <> expected.PrecisionValue)
           OR (expected.ScaleValue IS NOT NULL
               AND column_metadata.scale <> expected.ScaleValue)
           OR column_metadata.is_nullable <> expected.IsNullable)
        THROW 51000, 'Scheduling schema contract mismatch: column definition.', 1;

    DECLARE @finalSchedulingDefaults TABLE (
        SchemaName SYSNAME NOT NULL,
        TableName SYSNAME NOT NULL,
        ColumnName SYSNAME NOT NULL,
        ConstraintName SYSNAME NOT NULL,
        ExpectedDefinition NVARCHAR(200) NOT NULL
    );

    INSERT INTO @finalSchedulingDefaults
        (SchemaName, TableName, ColumnName, ConstraintName, ExpectedDefinition)
    VALUES
        (N'planning', N'SchedulingRequests', N'time_zone_id',
            N'DF_SchedulingRequests_TimeZoneId', N'''asia/ho_chi_minh'''),
        (N'planning', N'SchedulingRequests', N'return_to_start',
            N'DF_SchedulingRequests_ReturnToStart', N'1'),
        (N'planning', N'SchedulingRequests', N'transport_mode',
            N'DF_SchedulingRequests_TransportMode', N'''walking'''),
        (N'planning', N'SchedulingRequests', N'mandatory_poi_ids_json',
            N'DF_SchedulingRequests_MandatoryPoiIds', N'''[]'''),
        (N'planning', N'SchedulingRequests', N'rest_preference',
            N'DF_SchedulingRequests_RestPreference', N'''auto'''),
        (N'planning', N'ItineraryItems', N'item_kind',
            N'DF_ItineraryItems_ItemKind', N'''visit''');

    IF EXISTS (
        SELECT 1
        FROM @finalSchedulingDefaults AS expected
        INNER JOIN sys.default_constraints AS default_constraint
            ON default_constraint.name = expected.ConstraintName
        INNER JOIN sys.columns AS column_metadata
            ON column_metadata.object_id = default_constraint.parent_object_id
           AND column_metadata.column_id = default_constraint.parent_column_id
        WHERE (
                default_constraint.parent_object_id <> OBJECT_ID(
                    QUOTENAME(expected.SchemaName) + N'.' + QUOTENAME(expected.TableName))
                OR column_metadata.name <> expected.ColumnName
                OR REPLACE(
                       LOWER(REPLACE(REPLACE(REPLACE(
                           default_constraint.definition,
                           N'(', N''), N')', N''), N' ', N'')),
                       N'n''', N'''')
                    <> expected.ExpectedDefinition
              ))
        THROW 51000, 'Scheduling schema contract mismatch: default constraint.', 1;

    COMMIT TRANSACTION;
END TRY
BEGIN CATCH
    IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;
    THROW;
END CATCH;
GO
