/*
    TM-214: append-only recommendation feedback and behavioral signals.
    Additive and idempotent. Captured events are not consumed by scheduling.
*/
SET XACT_ABORT ON;
GO

BEGIN TRY
    BEGIN TRANSACTION;

    IF OBJECT_ID(N'dbo.Users', N'U') IS NULL
        THROW 51000, 'TM-214 migration requires dbo.Users.', 1;
    IF OBJECT_ID(N'catalog.POIs', N'U') IS NULL
        THROW 51000, 'TM-214 migration requires catalog.POIs.', 1;
    IF OBJECT_ID(N'planning.Itineraries', N'U') IS NULL
        THROW 51000, 'TM-214 migration requires planning.Itineraries.', 1;

    IF OBJECT_ID(N'social.RecommendationBehaviorEvents', N'U') IS NULL
    BEGIN
        IF OBJECT_ID(N'social.RecommendationBehaviorEvents') IS NOT NULL
            THROW 51000, 'TM-214 RecommendationBehaviorEvents object has the wrong type.', 1;

        CREATE TABLE social.RecommendationBehaviorEvents (
            event_id BIGINT IDENTITY(1,1) NOT NULL
                CONSTRAINT PK_RecommendationBehaviorEvents PRIMARY KEY,
            traveler_user_id BIGINT NOT NULL,
            poi_id BIGINT NOT NULL,
            itinerary_id BIGINT NULL,
            event_type VARCHAR(20) NOT NULL,
            original_position INT NULL,
            new_position INT NULL,
            was_mandatory BIT NULL,
            source VARCHAR(20) NOT NULL,
            occurred_at_utc DATETIME2 NOT NULL
                CONSTRAINT DF_RecommendationBehaviorEvents_OccurredAt
                DEFAULT SYSUTCDATETIME(),
            client_event_id UNIQUEIDENTIFIER NOT NULL,
            CONSTRAINT UQ_RecommendationBehaviorEvents_Traveler_Client
                UNIQUE (traveler_user_id, client_event_id),
            CONSTRAINT FK_RecommendationBehaviorEvents_Traveler
                FOREIGN KEY (traveler_user_id)
                REFERENCES dbo.Users(user_id) ON DELETE NO ACTION,
            CONSTRAINT FK_RecommendationBehaviorEvents_POI
                FOREIGN KEY (poi_id)
                REFERENCES catalog.POIs(poi_id) ON DELETE NO ACTION,
            CONSTRAINT FK_RecommendationBehaviorEvents_Itinerary
                FOREIGN KEY (itinerary_id)
                REFERENCES planning.Itineraries(itinerary_id) ON DELETE NO ACTION,
            CONSTRAINT CK_RecommendationBehaviorEvents_SourceContext CHECK (
                (source = 'Itinerary' AND itinerary_id IS NOT NULL
                    AND was_mandatory IS NOT NULL)
                OR (source IN ('Explore','PoiDetail') AND itinerary_id IS NULL
                    AND was_mandatory IS NULL)),
            CONSTRAINT CK_RecommendationBehaviorEvents_TypeSource CHECK (
                event_type IN ('Like','Dislike')
                OR (event_type IN ('Skip','Reorder') AND source = 'Itinerary'
                    AND itinerary_id IS NOT NULL)),
            CONSTRAINT CK_RecommendationBehaviorEvents_TypePositions CHECK (
                (event_type IN ('Like','Dislike')
                    AND original_position IS NULL AND new_position IS NULL)
                OR (event_type = 'Skip'
                    AND original_position IS NOT NULL AND new_position IS NULL)
                OR (event_type = 'Reorder'
                    AND original_position IS NOT NULL AND new_position IS NOT NULL
                    AND original_position <> new_position))
        );

        CREATE INDEX IX_RecommendationBehaviorEvents_Traveler_OccurredAt
            ON social.RecommendationBehaviorEvents(traveler_user_id, occurred_at_utc);
        CREATE INDEX IX_RecommendationBehaviorEvents_POI
            ON social.RecommendationBehaviorEvents(poi_id);
        CREATE INDEX IX_RecommendationBehaviorEvents_Itinerary
            ON social.RecommendationBehaviorEvents(itinerary_id);
    END;

    DECLARE @databaseCollation SYSNAME = CONVERT(
        SYSNAME, DATABASEPROPERTYEX(DB_NAME(), N'Collation'));

    IF (SELECT COUNT(*) FROM sys.columns
        WHERE object_id = OBJECT_ID(N'social.RecommendationBehaviorEvents')) <> 11
        THROW 51000, 'TM-214 RecommendationBehaviorEvents column count mismatch.', 1;

    IF EXISTS (
        SELECT expected.name
        FROM (VALUES
            (N'event_id', N'bigint', 8, 19, 0, 0, 1, CAST(NULL AS SYSNAME),
                CAST(NULL AS SYSNAME), CAST(NULL AS NVARCHAR(128))),
            (N'traveler_user_id', N'bigint', 8, 19, 0, 0, 0, NULL, NULL, NULL),
            (N'poi_id', N'bigint', 8, 19, 0, 0, 0, NULL, NULL, NULL),
            (N'itinerary_id', N'bigint', 8, 19, 0, 1, 0, NULL, NULL, NULL),
            (N'event_type', N'varchar', 20, 0, 0, 0, 0, @databaseCollation, NULL, NULL),
            (N'original_position', N'int', 4, 10, 0, 1, 0, NULL, NULL, NULL),
            (N'new_position', N'int', 4, 10, 0, 1, 0, NULL, NULL, NULL),
            (N'was_mandatory', N'bit', 1, 1, 0, 1, 0, NULL, NULL, NULL),
            (N'source', N'varchar', 20, 0, 0, 0, 0, @databaseCollation, NULL, NULL),
            (N'occurred_at_utc', N'datetime2', 8, 27, 7, 0, 0, NULL,
                N'DF_RecommendationBehaviorEvents_OccurredAt', N'sysutcdatetime'),
            (N'client_event_id', N'uniqueidentifier', 16, 0, 0, 0, 0, NULL, NULL, NULL)
        ) AS expected(
            name, type_name, max_length, precision_value, scale_value,
            is_nullable, is_identity, collation_name, default_name, default_definition)
        LEFT JOIN sys.columns AS actual
            ON actual.object_id = OBJECT_ID(N'social.RecommendationBehaviorEvents')
            AND actual.name = expected.name
        LEFT JOIN sys.types AS actual_type
            ON actual_type.user_type_id = actual.user_type_id
        LEFT JOIN sys.default_constraints AS default_constraint
            ON default_constraint.parent_object_id = actual.object_id
            AND default_constraint.parent_column_id = actual.column_id
        WHERE actual.column_id IS NULL
           OR actual_type.name <> expected.type_name
           OR actual.max_length <> expected.max_length
           OR (expected.precision_value > 0 AND actual.precision <> expected.precision_value)
           OR actual.scale <> expected.scale_value
           OR actual.is_nullable <> expected.is_nullable
           OR actual.is_identity <> expected.is_identity
           OR ISNULL(actual.collation_name, N'<null>')
              <> ISNULL(expected.collation_name, N'<null>')
           OR ISNULL(default_constraint.name, N'<null>')
              <> ISNULL(expected.default_name, N'<null>')
           OR ISNULL(LOWER(REPLACE(REPLACE(REPLACE(
                default_constraint.definition, N'(', N''), N')', N''), N' ', N'')), N'<null>')
              <> ISNULL(expected.default_definition, N'<null>'))
        THROW 51000, 'TM-214 RecommendationBehaviorEvents column/default shape mismatch.', 1;

    DECLARE @primaryKeyIndexId INT;
    SELECT @primaryKeyIndexId = key_constraint.unique_index_id
    FROM sys.key_constraints AS key_constraint
    WHERE key_constraint.parent_object_id = OBJECT_ID(N'social.RecommendationBehaviorEvents')
      AND key_constraint.name = N'PK_RecommendationBehaviorEvents'
      AND key_constraint.type = N'PK';

    IF @primaryKeyIndexId IS NULL
       OR INDEX_COL(N'social.RecommendationBehaviorEvents', @primaryKeyIndexId, 1) <> N'event_id'
       OR INDEX_COL(N'social.RecommendationBehaviorEvents', @primaryKeyIndexId, 2) IS NOT NULL
        THROW 51000, 'TM-214 RecommendationBehaviorEvents primary key mismatch.', 1;

    IF (SELECT COUNT(*) FROM sys.foreign_keys
        WHERE parent_object_id = OBJECT_ID(N'social.RecommendationBehaviorEvents')) <> 3
        THROW 51000, 'TM-214 RecommendationBehaviorEvents foreign key count mismatch.', 1;

    IF EXISTS (
        SELECT expected.constraint_name
        FROM (VALUES
            (N'FK_RecommendationBehaviorEvents_Traveler', N'traveler_user_id',
                N'dbo.Users', N'user_id'),
            (N'FK_RecommendationBehaviorEvents_POI', N'poi_id',
                N'catalog.POIs', N'poi_id'),
            (N'FK_RecommendationBehaviorEvents_Itinerary', N'itinerary_id',
                N'planning.Itineraries', N'itinerary_id')
        ) AS expected(constraint_name, parent_column, referenced_table, referenced_column)
        LEFT JOIN sys.foreign_keys AS foreign_key
            ON foreign_key.parent_object_id = OBJECT_ID(N'social.RecommendationBehaviorEvents')
            AND foreign_key.name = expected.constraint_name
        LEFT JOIN sys.foreign_key_columns AS column_map
            ON column_map.constraint_object_id = foreign_key.object_id
            AND column_map.constraint_column_id = 1
        WHERE foreign_key.object_id IS NULL
           OR foreign_key.referenced_object_id <> OBJECT_ID(expected.referenced_table)
           OR foreign_key.delete_referential_action <> 0
           OR foreign_key.update_referential_action <> 0
           OR foreign_key.is_disabled <> 0
           OR foreign_key.is_not_trusted <> 0
           OR COL_NAME(column_map.parent_object_id, column_map.parent_column_id)
              <> expected.parent_column
           OR COL_NAME(column_map.referenced_object_id, column_map.referenced_column_id)
              <> expected.referenced_column)
        THROW 51000, 'TM-214 RecommendationBehaviorEvents foreign key mismatch.', 1;

    IF (SELECT COUNT(*) FROM sys.check_constraints
        WHERE parent_object_id = OBJECT_ID(N'social.RecommendationBehaviorEvents')) <> 3
       OR EXISTS (
            SELECT 1 FROM sys.check_constraints
            WHERE parent_object_id = OBJECT_ID(N'social.RecommendationBehaviorEvents')
              AND (is_disabled = 1 OR is_not_trusted = 1))
        THROW 51000, 'TM-214 RecommendationBehaviorEvents check inventory mismatch.', 1;

    DECLARE @sourceContextCheck NVARCHAR(MAX) = LOWER(REPLACE(REPLACE(REPLACE(REPLACE(
        OBJECT_DEFINITION(OBJECT_ID(N'social.CK_RecommendationBehaviorEvents_SourceContext', N'C')),
        N'[', N''), N']', N''), N'(', N''), N')', N''));
    SET @sourceContextCheck = REPLACE(REPLACE(REPLACE(
        @sourceContextCheck, N' ', N''), CHAR(13), N''), CHAR(10), N'');
    IF @sourceContextCheck IS NULL OR @sourceContextCheck <>
        N'source=''itinerary''anditinerary_idisnotnullandwas_mandatoryisnotnullorsource=''poidetail''orsource=''explore''anditinerary_idisnullandwas_mandatoryisnull'
        THROW 51000, 'TM-214 source/context check mismatch.', 1;

    DECLARE @typeSourceCheck NVARCHAR(MAX) = LOWER(REPLACE(REPLACE(REPLACE(REPLACE(
        OBJECT_DEFINITION(OBJECT_ID(N'social.CK_RecommendationBehaviorEvents_TypeSource', N'C')),
        N'[', N''), N']', N''), N'(', N''), N')', N''));
    SET @typeSourceCheck = REPLACE(REPLACE(REPLACE(
        @typeSourceCheck, N' ', N''), CHAR(13), N''), CHAR(10), N'');
    IF @typeSourceCheck IS NULL OR @typeSourceCheck <>
        N'event_type=''dislike''orevent_type=''like''orevent_type=''reorder''orevent_type=''skip''andsource=''itinerary''anditinerary_idisnotnull'
        THROW 51000, 'TM-214 type/source check mismatch.', 1;

    DECLARE @typePositionsCheck NVARCHAR(MAX) = LOWER(REPLACE(REPLACE(REPLACE(REPLACE(
        OBJECT_DEFINITION(OBJECT_ID(N'social.CK_RecommendationBehaviorEvents_TypePositions', N'C')),
        N'[', N''), N']', N''), N'(', N''), N')', N''));
    SET @typePositionsCheck = REPLACE(REPLACE(REPLACE(
        @typePositionsCheck, N' ', N''), CHAR(13), N''), CHAR(10), N'');
    IF @typePositionsCheck IS NULL OR @typePositionsCheck <>
        N'event_type=''dislike''orevent_type=''like''andoriginal_positionisnullandnew_positionisnullorevent_type=''skip''andoriginal_positionisnotnullandnew_positionisnullorevent_type=''reorder''andoriginal_positionisnotnullandnew_positionisnotnullandoriginal_position<>new_position'
        THROW 51000, 'TM-214 type/positions check mismatch.', 1;

    IF (SELECT COUNT(*) FROM sys.indexes
        WHERE object_id = OBJECT_ID(N'social.RecommendationBehaviorEvents')
          AND index_id > 0 AND is_hypothetical = 0) <> 5
        THROW 51000, 'TM-214 RecommendationBehaviorEvents index count mismatch.', 1;

    DECLARE @clientIndexId INT = INDEXPROPERTY(
        OBJECT_ID(N'social.RecommendationBehaviorEvents'),
        N'UQ_RecommendationBehaviorEvents_Traveler_Client', N'IndexId');
    IF @clientIndexId IS NULL
       OR INDEX_COL(N'social.RecommendationBehaviorEvents', @clientIndexId, 1) <> N'traveler_user_id'
       OR INDEX_COL(N'social.RecommendationBehaviorEvents', @clientIndexId, 2) <> N'client_event_id'
       OR INDEX_COL(N'social.RecommendationBehaviorEvents', @clientIndexId, 3) IS NOT NULL
       OR NOT EXISTS (
            SELECT 1 FROM sys.indexes
            WHERE object_id = OBJECT_ID(N'social.RecommendationBehaviorEvents')
              AND index_id = @clientIndexId AND is_unique = 1 AND is_disabled = 0)
        THROW 51000, 'TM-214 client event uniqueness mismatch.', 1;

    IF EXISTS (
        SELECT expected.index_name
        FROM (VALUES
            (N'IX_RecommendationBehaviorEvents_Traveler_OccurredAt',
                N'traveler_user_id', N'occurred_at_utc'),
            (N'IX_RecommendationBehaviorEvents_POI', N'poi_id', CAST(NULL AS SYSNAME)),
            (N'IX_RecommendationBehaviorEvents_Itinerary', N'itinerary_id', NULL)
        ) AS expected(index_name, first_column, second_column)
        LEFT JOIN sys.indexes AS actual
            ON actual.object_id = OBJECT_ID(N'social.RecommendationBehaviorEvents')
            AND actual.name = expected.index_name
        WHERE actual.index_id IS NULL
           OR actual.is_unique <> 0
           OR actual.is_disabled <> 0
           OR actual.has_filter <> 0
           OR INDEX_COL(N'social.RecommendationBehaviorEvents', actual.index_id, 1)
              <> expected.first_column
           OR ISNULL(INDEX_COL(N'social.RecommendationBehaviorEvents', actual.index_id, 2), N'<null>')
              <> ISNULL(expected.second_column, N'<null>')
           OR INDEX_COL(N'social.RecommendationBehaviorEvents', actual.index_id, 3) IS NOT NULL)
        THROW 51000, 'TM-214 RecommendationBehaviorEvents lookup index mismatch.', 1;

    COMMIT TRANSACTION;
END TRY
BEGIN CATCH
    IF XACT_STATE() <> 0 ROLLBACK TRANSACTION;
    THROW;
END CATCH;
GO
