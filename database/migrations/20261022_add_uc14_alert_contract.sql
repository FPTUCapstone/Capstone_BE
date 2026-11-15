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

    IF OBJECT_ID(N'trip.Incidents', N'U') IS NULL
       OR OBJECT_ID(N'trip.TripSessionItems', N'U') IS NULL
       OR OBJECT_ID(N'dbo.Notifications', N'U') IS NULL
       OR OBJECT_ID(N'dbo.Users', N'U') IS NULL
        THROW 51014, 'UC-14 prerequisites are missing.', 1;

    IF COL_LENGTH(N'trip.Incidents', N'affected_itinerary_item_id') IS NULL
        ALTER TABLE trip.Incidents ADD affected_itinerary_item_id BIGINT NULL;

    IF COL_LENGTH(N'trip.Incidents', N'source_key') IS NULL
        ALTER TABLE trip.Incidents ADD source_key NVARCHAR(300) NULL;

    IF COL_LENGTH(N'trip.Incidents', N'client_event_id') IS NULL
        ALTER TABLE trip.Incidents ADD client_event_id UNIQUEIDENTIFIER NULL;

    IF COL_LENGTH(N'dbo.Notifications', N'read_at') IS NULL
        ALTER TABLE dbo.Notifications ADD read_at DATETIME2 NULL;

    IF NOT EXISTS (
        SELECT 1 FROM sys.foreign_keys
        WHERE parent_object_id = OBJECT_ID(N'trip.Incidents')
          AND name = N'FK_Incidents_AffectedTripSessionItem')
        EXEC(N'ALTER TABLE trip.Incidents WITH CHECK
            ADD CONSTRAINT FK_Incidents_AffectedTripSessionItem
            FOREIGN KEY (session_id, affected_itinerary_item_id)
            REFERENCES trip.TripSessionItems(session_id, itinerary_item_id);');

    -- Dynamic SQL defers name resolution until after the columns above exist. SQL Server
    -- otherwise compiles this entire batch before executing the ALTER TABLE statements.
    EXEC(N'IF EXISTS (
        SELECT 1 FROM trip.Incidents
        WHERE resolved_at IS NULL AND source_key IS NOT NULL
        GROUP BY session_id, incident_type, source_key
        HAVING COUNT_BIG(*) > 1)
        THROW 51014, ''UC-14 migration blocked: duplicate open incident source keys.'', 1;');

    EXEC(N'IF EXISTS (
        SELECT 1 FROM trip.Incidents
        WHERE client_event_id IS NOT NULL
        GROUP BY session_id, client_event_id
        HAVING COUNT_BIG(*) > 1)
        THROW 51014, ''UC-14 migration blocked: duplicate client event IDs.'', 1;');

    IF NOT EXISTS (
        SELECT 1 FROM sys.indexes
        WHERE object_id = OBJECT_ID(N'trip.Incidents')
          AND name = N'UQ_Incidents_OpenSource')
        EXEC(N'CREATE UNIQUE INDEX UQ_Incidents_OpenSource
            ON trip.Incidents(session_id, incident_type, source_key)
            WHERE resolved_at IS NULL AND source_key IS NOT NULL;');

    IF NOT EXISTS (
        SELECT 1 FROM sys.indexes
        WHERE object_id = OBJECT_ID(N'trip.Incidents')
          AND name = N'UQ_Incidents_ClientEvent')
        EXEC(N'CREATE UNIQUE INDEX UQ_Incidents_ClientEvent
            ON trip.Incidents(session_id, client_event_id)
            WHERE client_event_id IS NOT NULL;');

    IF EXISTS (
        SELECT 1 FROM dbo.Notifications
        WHERE type = 'TripAlert'
          AND related_entity_type = 'Incident'
          AND related_entity_id IS NOT NULL
        GROUP BY user_id, related_entity_id
        HAVING COUNT_BIG(*) > 1)
        THROW 51014, 'UC-14 migration blocked: duplicate TripAlert notifications.', 1;

    IF NOT EXISTS (
        SELECT 1 FROM sys.indexes
        WHERE object_id = OBJECT_ID(N'dbo.Notifications')
          AND name = N'UQ_Notifications_TripAlertIncident')
        CREATE UNIQUE INDEX UQ_Notifications_TripAlertIncident
            ON dbo.Notifications(user_id, related_entity_id)
            WHERE type = 'TripAlert'
              AND related_entity_type = 'Incident'
              AND related_entity_id IS NOT NULL;

    IF OBJECT_ID(N'trip.AlertDeliveryWork', N'U') IS NULL
    BEGIN
        CREATE TABLE trip.AlertDeliveryWork (
            notification_id       BIGINT NOT NULL,
            attempt_count         INT NOT NULL CONSTRAINT DF_AlertDeliveryWork_AttemptCount DEFAULT 0,
            next_attempt_at       DATETIME2 NULL,
            claim_token           UNIQUEIDENTIFIER NULL,
            claim_expires_at      DATETIME2 NULL,
            CONSTRAINT PK_AlertDeliveryWork PRIMARY KEY (notification_id),
            CONSTRAINT FK_AlertDeliveryWork_Notification FOREIGN KEY (notification_id)
                REFERENCES dbo.Notifications(notification_id) ON DELETE CASCADE,
            CONSTRAINT CK_AlertDeliveryWork_AttemptCount CHECK (attempt_count BETWEEN 0 AND 3),
            CONSTRAINT CK_AlertDeliveryWork_Claim CHECK (
                (claim_token IS NULL AND claim_expires_at IS NULL)
                OR (claim_token IS NOT NULL AND claim_expires_at IS NOT NULL))
        );
    END;

    IF NOT EXISTS (
        SELECT 1 FROM sys.indexes
        WHERE object_id = OBJECT_ID(N'trip.AlertDeliveryWork')
          AND name = N'IX_AlertDeliveryWork_Due')
        CREATE INDEX IX_AlertDeliveryWork_Due
            ON trip.AlertDeliveryWork(next_attempt_at, claim_expires_at);

    IF OBJECT_ID(N'dbo.PushDeviceTokens', N'U') IS NULL
    BEGIN
        CREATE TABLE dbo.PushDeviceTokens (
            push_device_token_id BIGINT IDENTITY(1,1) NOT NULL,
            installation_id     UNIQUEIDENTIFIER NOT NULL,
            user_id             BIGINT NOT NULL,
            protected_token     NVARCHAR(2000) NOT NULL,
            token_hash          CHAR(64) NOT NULL,
            created_at          DATETIME2 NOT NULL,
            updated_at          DATETIME2 NOT NULL,
            deactivated_at      DATETIME2 NULL,
            CONSTRAINT PK_PushDeviceTokens PRIMARY KEY (push_device_token_id),
            CONSTRAINT FK_PushDeviceTokens_User FOREIGN KEY (user_id)
                REFERENCES dbo.Users(user_id) ON DELETE CASCADE
        );
    END;

    IF NOT EXISTS (
        SELECT 1 FROM sys.indexes
        WHERE object_id = OBJECT_ID(N'dbo.PushDeviceTokens')
          AND name = N'UQ_PushDeviceTokens_Installation')
        CREATE UNIQUE INDEX UQ_PushDeviceTokens_Installation
            ON dbo.PushDeviceTokens(installation_id);

    IF NOT EXISTS (
        SELECT 1 FROM sys.indexes
        WHERE object_id = OBJECT_ID(N'dbo.PushDeviceTokens')
          AND name = N'UQ_PushDeviceTokens_TokenHash')
        CREATE UNIQUE INDEX UQ_PushDeviceTokens_TokenHash
            ON dbo.PushDeviceTokens(token_hash);

    IF NOT EXISTS (
        SELECT 1 FROM sys.indexes
        WHERE object_id = OBJECT_ID(N'dbo.PushDeviceTokens')
          AND name = N'IX_PushDeviceTokens_User_Active')
        CREATE INDEX IX_PushDeviceTokens_User_Active
            ON dbo.PushDeviceTokens(user_id, deactivated_at);

    COMMIT TRANSACTION;
END TRY
BEGIN CATCH
    IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;
    THROW;
END CATCH;
GO
