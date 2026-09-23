SET XACT_ABORT ON;
SET NOCOUNT ON;

BEGIN TRY
    BEGIN TRANSACTION;

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

    COMMIT TRANSACTION;
END TRY
BEGIN CATCH
    IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;
    THROW;
END CATCH;
GO
