SET XACT_ABORT ON;
SET NOCOUNT ON;

BEGIN TRY
    BEGIN TRANSACTION;

    IF OBJECT_ID(N'social.TravelGroupCreationRequests', N'U') IS NULL
    BEGIN
        CREATE TABLE social.TravelGroupCreationRequests (
            request_id              BIGINT IDENTITY(1,1) PRIMARY KEY,
            traveler_user_id        BIGINT NOT NULL REFERENCES dbo.Users(user_id),
            idempotency_key         UNIQUEIDENTIFIER NOT NULL,
            itinerary_id            BIGINT NOT NULL REFERENCES planning.Itineraries(itinerary_id),
            group_name              NVARCHAR(150) NOT NULL,
            group_id                BIGINT NOT NULL REFERENCES social.TravelGroups(group_id) ON DELETE CASCADE,
            created_at              DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(),
            CONSTRAINT UQ_TravelGroupCreationRequests_TravelerKey
                UNIQUE (traveler_user_id, idempotency_key)
        );
    END;
    ELSE
    BEGIN
        IF COL_LENGTH(N'social.TravelGroupCreationRequests', N'itinerary_id') IS NULL
        BEGIN
            EXEC(N'ALTER TABLE social.TravelGroupCreationRequests ADD itinerary_id BIGINT NULL;');
        END;

        EXEC sys.sp_executesql N'
            UPDATE request
            SET itinerary_id = travel_group.itinerary_id
            FROM social.TravelGroupCreationRequests request
            INNER JOIN social.TravelGroups travel_group ON travel_group.group_id = request.group_id
            WHERE request.itinerary_id IS NULL;

            IF EXISTS (
                SELECT 1
                FROM social.TravelGroupCreationRequests
                WHERE itinerary_id IS NULL)
            BEGIN
                THROW 51000, ''Cannot backfill itinerary_id for TravelGroupCreationRequests.'', 1;
            END;';

        IF EXISTS (
            SELECT 1
            FROM sys.columns
            WHERE object_id = OBJECT_ID(N'social.TravelGroupCreationRequests')
              AND name = N'itinerary_id'
              AND is_nullable = 1)
        BEGIN
            EXEC(N'ALTER TABLE social.TravelGroupCreationRequests ALTER COLUMN itinerary_id BIGINT NOT NULL;');
        END;

        IF NOT EXISTS (
            SELECT 1
            FROM sys.foreign_keys
            WHERE name = N'FK_TravelGroupCreationRequests_Itinerary'
              AND parent_object_id = OBJECT_ID(N'social.TravelGroupCreationRequests')
              AND referenced_object_id = OBJECT_ID(N'planning.Itineraries'))
        BEGIN
            EXEC(N'
                ALTER TABLE social.TravelGroupCreationRequests
                    ADD CONSTRAINT FK_TravelGroupCreationRequests_Itinerary
                    FOREIGN KEY (itinerary_id) REFERENCES planning.Itineraries(itinerary_id);');
        END;

        IF COL_LENGTH(N'social.TravelGroupCreationRequests', N'group_name') IS NULL
        BEGIN
            EXEC(N'ALTER TABLE social.TravelGroupCreationRequests ADD group_name NVARCHAR(150) NULL;');
        END;

        EXEC sys.sp_executesql N'
            UPDATE request
            SET group_name = COALESCE(travel_group.name, N''Unnamed Group'')
            FROM social.TravelGroupCreationRequests request
            INNER JOIN social.TravelGroups travel_group ON travel_group.group_id = request.group_id
            WHERE request.group_name IS NULL;

            IF EXISTS (
                SELECT 1
                FROM social.TravelGroupCreationRequests
                WHERE group_name IS NULL)
            BEGIN
                THROW 51000, ''Cannot backfill group_name for TravelGroupCreationRequests.'', 1;
            END;';

        IF EXISTS (
            SELECT 1
            FROM sys.columns
            WHERE object_id = OBJECT_ID(N'social.TravelGroupCreationRequests')
              AND name = N'group_name'
              AND is_nullable = 1)
        BEGIN
            EXEC(N'ALTER TABLE social.TravelGroupCreationRequests ALTER COLUMN group_name NVARCHAR(150) NOT NULL;');
        END;
    END;

    COMMIT TRANSACTION;
END TRY
BEGIN CATCH
    IF @@TRANCOUNT > 0
    BEGIN
        ROLLBACK TRANSACTION;
    END;

    THROW;
END CATCH;
GO

