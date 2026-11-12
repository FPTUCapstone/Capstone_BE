/* UC-70 weather ingestion for Operator review only: the durable webhook inbox, provider
   evidence on trip.WeatherEvents, the Operator's WeatherSafetyReviews and the Tomorrow.io
   Location mappings. Nothing here cancels or stops a departure.
   Idempotent: objects are created only when missing, every shape is checked, and all
   constraints end enabled and trusted; any mismatch raises and the script rolls back. */
SET XACT_ABORT ON;
GO

BEGIN TRY
    BEGIN TRANSACTION;

    IF OBJECT_ID(N'commerce.TourSchedules', N'U') IS NULL
        THROW 51000, 'Weather Operator review requires commerce.TourSchedules.', 1;
    IF OBJECT_ID(N'trip.WeatherEvents', N'U') IS NULL
        THROW 51000, 'Weather Operator review requires trip.WeatherEvents.', 1;

    /* 1. Durable inbox for authenticated deliveries and reconciled notifications. */
    IF OBJECT_ID(N'trip.WeatherAlertInboxItems', N'U') IS NULL
        CREATE TABLE trip.WeatherAlertInboxItems (
            inbox_id BIGINT IDENTITY(1,1) NOT NULL CONSTRAINT PK_WeatherAlertInboxItems PRIMARY KEY,
            provider VARCHAR(50) NOT NULL,
            provider_notification_id VARCHAR(150) NULL,
            payload NVARCHAR(MAX) NOT NULL,
            status VARCHAR(30) NOT NULL CONSTRAINT DF_WeatherAlertInboxItems_Status DEFAULT 'Pending',
            attempt_count INT NOT NULL CONSTRAINT DF_WeatherAlertInboxItems_Attempts DEFAULT 0,
            max_attempts INT NOT NULL CONSTRAINT DF_WeatherAlertInboxItems_MaxAttempts DEFAULT 3,
            lease_expires_at DATETIME2 NULL,
            leased_by VARCHAR(100) NULL,
            last_error NVARCHAR(1000) NULL,
            received_at DATETIME2 NOT NULL,
            processed_at DATETIME2 NULL,
            CONSTRAINT CK_WeatherAlertInboxItems_Status CHECK (status IN ('Pending','Processing','Processed','Failed','Discarded')),
            CONSTRAINT CK_WeatherAlertInboxItems_Attempts CHECK (attempt_count >= 0 AND max_attempts > 0)
        );

    IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'trip.WeatherAlertInboxItems') AND name = N'UX_WeatherAlertInboxItems_ProviderNotification')
        CREATE UNIQUE NONCLUSTERED INDEX UX_WeatherAlertInboxItems_ProviderNotification
            ON trip.WeatherAlertInboxItems (provider, provider_notification_id) WHERE provider_notification_id IS NOT NULL;
    IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'trip.WeatherAlertInboxItems') AND name = N'IX_WeatherAlertInboxItems_StatusLease')
        CREATE NONCLUSTERED INDEX IX_WeatherAlertInboxItems_StatusLease
            ON trip.WeatherAlertInboxItems (status, lease_expires_at);

    /* 2. Provider evidence on the existing UC-59 weather table. */
    IF COL_LENGTH(N'trip.WeatherEvents', N'provider') IS NULL ALTER TABLE trip.WeatherEvents ADD provider VARCHAR(50) NULL;
    IF COL_LENGTH(N'trip.WeatherEvents', N'provider_event_id') IS NULL ALTER TABLE trip.WeatherEvents ADD provider_event_id VARCHAR(150) NULL;
    IF COL_LENGTH(N'trip.WeatherEvents', N'original_authority') IS NULL ALTER TABLE trip.WeatherEvents ADD original_authority NVARCHAR(200) NULL;
    IF COL_LENGTH(N'trip.WeatherEvents', N'official_vietnam_risk_level') IS NULL ALTER TABLE trip.WeatherEvents ADD official_vietnam_risk_level INT NULL;
    IF COL_LENGTH(N'trip.WeatherEvents', N'verified_directive') IS NULL ALTER TABLE trip.WeatherEvents ADD verified_directive VARCHAR(50) NULL;
    IF COL_LENGTH(N'trip.WeatherEvents', N'is_withdrawn') IS NULL ALTER TABLE trip.WeatherEvents ADD is_withdrawn BIT NOT NULL CONSTRAINT DF_WeatherEvents_IsWithdrawn DEFAULT 0;
    IF COL_LENGTH(N'trip.WeatherEvents', N'updated_at') IS NULL ALTER TABLE trip.WeatherEvents ADD updated_at DATETIME2 NULL;

    IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'trip.WeatherEvents') AND name = N'UX_WeatherEvents_ProviderEvent')
        EXEC sys.sp_executesql N'CREATE UNIQUE NONCLUSTERED INDEX UX_WeatherEvents_ProviderEvent ON trip.WeatherEvents (provider, provider_event_id)
            WHERE provider IS NOT NULL AND provider_event_id IS NOT NULL;';
    IF NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE parent_object_id = OBJECT_ID(N'trip.WeatherEvents') AND name = N'CK_WeatherEvents_RiskLevel')
        EXEC sys.sp_executesql N'ALTER TABLE trip.WeatherEvents WITH CHECK ADD CONSTRAINT CK_WeatherEvents_RiskLevel
            CHECK (official_vietnam_risk_level IS NULL OR official_vietnam_risk_level BETWEEN 1 AND 5);';

    /* 3. One Operator review per departure and provider event. */
    IF OBJECT_ID(N'trip.WeatherSafetyReviews', N'U') IS NULL
        CREATE TABLE trip.WeatherSafetyReviews (
            review_id BIGINT IDENTITY(1,1) NOT NULL CONSTRAINT PK_WeatherSafetyReviews PRIMARY KEY,
            tour_schedule_id BIGINT NOT NULL CONSTRAINT FK_WeatherSafetyReviews_TourSchedule REFERENCES commerce.TourSchedules(schedule_id),
            provider VARCHAR(50) NOT NULL,
            provider_event_id VARCHAR(150) NOT NULL,
            status VARCHAR(30) NOT NULL CONSTRAINT DF_WeatherSafetyReviews_Status DEFAULT 'PendingAcknowledgement',
            urgent_notice_sent_at DATETIME2 NOT NULL,
            acknowledgement_deadline DATETIME2 NOT NULL,
            acknowledged_at DATETIME2 NULL,
            acknowledged_by_operator_id BIGINT NULL CONSTRAINT FK_WeatherSafetyReviews_Operator REFERENCES dbo.Users(user_id),
            escalated_at DATETIME2 NULL,
            escalation_contact NVARCHAR(200) NULL,
            immediate_instructions NVARCHAR(1000) NULL,
            created_at DATETIME2 NOT NULL,
            CONSTRAINT CK_WeatherSafetyReviews_Status CHECK (status IN ('PendingAcknowledgement','Acknowledged','Escalated','Closed'))
        );

    IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'trip.WeatherSafetyReviews') AND name = N'UX_WeatherSafetyReviews_ScheduleEvent')
        CREATE UNIQUE NONCLUSTERED INDEX UX_WeatherSafetyReviews_ScheduleEvent
            ON trip.WeatherSafetyReviews (tour_schedule_id, provider, provider_event_id);
    -- The escalation sweep: status = 'PendingAcknowledgement' AND acknowledgement_deadline <= now.
    IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'trip.WeatherSafetyReviews') AND name = N'IX_WeatherSafetyReviews_StatusDeadline')
        CREATE NONCLUSTERED INDEX IX_WeatherSafetyReviews_StatusDeadline
            ON trip.WeatherSafetyReviews (status, acknowledgement_deadline);

    /* 4. TripMate points registered as Tomorrow.io Locations. */
    IF OBJECT_ID(N'trip.WeatherLocationMappings', N'U') IS NULL
        CREATE TABLE trip.WeatherLocationMappings (
            location_mapping_id BIGINT IDENTITY(1,1) NOT NULL CONSTRAINT PK_WeatherLocationMappings PRIMARY KEY,
            trip_mate_location_key VARCHAR(100) NOT NULL,
            tomorrow_location_id VARCHAR(100) NOT NULL,
            latitude DECIMAL(9,6) NOT NULL,
            longitude DECIMAL(9,6) NOT NULL,
            name NVARCHAR(200) NULL,
            last_departure_end_at DATETIME2 NULL,
            is_active BIT NOT NULL CONSTRAINT DF_WeatherLocationMappings_IsActive DEFAULT 1,
            created_at DATETIME2 NOT NULL,
            updated_at DATETIME2 NOT NULL,
            CONSTRAINT CK_WeatherLocationMappings_Coordinates CHECK (latitude BETWEEN -90 AND 90 AND longitude BETWEEN -180 AND 180)
        );

    IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'trip.WeatherLocationMappings') AND name = N'UX_WeatherLocationMappings_Key')
        CREATE UNIQUE NONCLUSTERED INDEX UX_WeatherLocationMappings_Key ON trip.WeatherLocationMappings (trip_mate_location_key);
    IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'trip.WeatherLocationMappings') AND name = N'UX_WeatherLocationMappings_TomorrowId')
        CREATE UNIQUE NONCLUSTERED INDEX UX_WeatherLocationMappings_TomorrowId ON trip.WeatherLocationMappings (tomorrow_location_id);

    /* 5. Shape guard: a rerun over a table with another shape fails instead of drifting. */
    DECLARE @expected TABLE (table_name sysname, column_name sysname, type_name sysname, max_length SMALLINT, is_nullable BIT);
    INSERT INTO @expected VALUES
        (N'trip.WeatherAlertInboxItems', N'inbox_id', N'bigint', 8, 0),
        (N'trip.WeatherAlertInboxItems', N'provider', N'varchar', 50, 0),
        (N'trip.WeatherAlertInboxItems', N'provider_notification_id', N'varchar', 150, 1),
        (N'trip.WeatherAlertInboxItems', N'payload', N'nvarchar', -1, 0),
        (N'trip.WeatherAlertInboxItems', N'status', N'varchar', 30, 0),
        (N'trip.WeatherAlertInboxItems', N'attempt_count', N'int', 4, 0),
        (N'trip.WeatherAlertInboxItems', N'max_attempts', N'int', 4, 0),
        (N'trip.WeatherAlertInboxItems', N'lease_expires_at', N'datetime2', 8, 1),
        (N'trip.WeatherAlertInboxItems', N'leased_by', N'varchar', 100, 1),
        (N'trip.WeatherAlertInboxItems', N'last_error', N'nvarchar', 2000, 1),
        (N'trip.WeatherAlertInboxItems', N'received_at', N'datetime2', 8, 0),
        (N'trip.WeatherAlertInboxItems', N'processed_at', N'datetime2', 8, 1),
        (N'trip.WeatherSafetyReviews', N'review_id', N'bigint', 8, 0),
        (N'trip.WeatherSafetyReviews', N'tour_schedule_id', N'bigint', 8, 0),
        (N'trip.WeatherSafetyReviews', N'provider', N'varchar', 50, 0),
        (N'trip.WeatherSafetyReviews', N'provider_event_id', N'varchar', 150, 0),
        (N'trip.WeatherSafetyReviews', N'status', N'varchar', 30, 0),
        (N'trip.WeatherSafetyReviews', N'urgent_notice_sent_at', N'datetime2', 8, 0),
        (N'trip.WeatherSafetyReviews', N'acknowledgement_deadline', N'datetime2', 8, 0),
        (N'trip.WeatherSafetyReviews', N'acknowledged_at', N'datetime2', 8, 1),
        (N'trip.WeatherSafetyReviews', N'acknowledged_by_operator_id', N'bigint', 8, 1),
        (N'trip.WeatherSafetyReviews', N'escalated_at', N'datetime2', 8, 1),
        (N'trip.WeatherSafetyReviews', N'escalation_contact', N'nvarchar', 400, 1),
        (N'trip.WeatherSafetyReviews', N'immediate_instructions', N'nvarchar', 2000, 1),
        (N'trip.WeatherSafetyReviews', N'created_at', N'datetime2', 8, 0),
        (N'trip.WeatherLocationMappings', N'location_mapping_id', N'bigint', 8, 0),
        (N'trip.WeatherLocationMappings', N'trip_mate_location_key', N'varchar', 100, 0),
        (N'trip.WeatherLocationMappings', N'tomorrow_location_id', N'varchar', 100, 0),
        (N'trip.WeatherLocationMappings', N'latitude', N'decimal', 5, 0),
        (N'trip.WeatherLocationMappings', N'longitude', N'decimal', 5, 0),
        (N'trip.WeatherLocationMappings', N'name', N'nvarchar', 400, 1),
        (N'trip.WeatherLocationMappings', N'last_departure_end_at', N'datetime2', 8, 1),
        (N'trip.WeatherLocationMappings', N'is_active', N'bit', 1, 0),
        (N'trip.WeatherLocationMappings', N'created_at', N'datetime2', 8, 0),
        (N'trip.WeatherLocationMappings', N'updated_at', N'datetime2', 8, 0),
        (N'trip.WeatherEvents', N'provider', N'varchar', 50, 1),
        (N'trip.WeatherEvents', N'provider_event_id', N'varchar', 150, 1),
        (N'trip.WeatherEvents', N'original_authority', N'nvarchar', 400, 1),
        (N'trip.WeatherEvents', N'official_vietnam_risk_level', N'int', 4, 1),
        (N'trip.WeatherEvents', N'verified_directive', N'varchar', 50, 1),
        (N'trip.WeatherEvents', N'is_withdrawn', N'bit', 1, 0),
        (N'trip.WeatherEvents', N'updated_at', N'datetime2', 8, 1);

    IF EXISTS (
        SELECT expected.table_name, expected.column_name, expected.type_name, expected.max_length, expected.is_nullable
        FROM @expected AS expected
        EXCEPT
        SELECT OBJECT_SCHEMA_NAME(column_info.object_id) + N'.' + OBJECT_NAME(column_info.object_id),
            column_info.name, type_info.name, column_info.max_length, column_info.is_nullable
        FROM sys.columns AS column_info
        INNER JOIN sys.types AS type_info ON type_info.user_type_id = column_info.user_type_id
        WHERE column_info.object_id IN (OBJECT_ID(N'trip.WeatherAlertInboxItems'), OBJECT_ID(N'trip.WeatherSafetyReviews'),
            OBJECT_ID(N'trip.WeatherLocationMappings'), OBJECT_ID(N'trip.WeatherEvents')))
        THROW 51000, 'UC-70 weather tables have an incompatible column shape.', 1;

    -- The three UC-70 tables carry exactly the columns above; WeatherEvents also keeps its UC-59 columns.
    IF EXISTS (
        SELECT 1
        FROM (VALUES (N'trip.WeatherAlertInboxItems', 12), (N'trip.WeatherSafetyReviews', 13),
            (N'trip.WeatherLocationMappings', 10)) AS expected(table_name, column_count)
        WHERE (SELECT COUNT(*) FROM sys.columns WHERE object_id = OBJECT_ID(expected.table_name)) <> expected.column_count)
        THROW 51000, 'UC-70 weather tables have unexpected columns.', 1;

    ALTER TABLE trip.WeatherAlertInboxItems WITH CHECK CHECK CONSTRAINT ALL;
    ALTER TABLE trip.WeatherEvents WITH CHECK CHECK CONSTRAINT ALL;
    ALTER TABLE trip.WeatherSafetyReviews WITH CHECK CHECK CONSTRAINT ALL;
    ALTER TABLE trip.WeatherLocationMappings WITH CHECK CHECK CONSTRAINT ALL;

    COMMIT TRANSACTION;
END TRY
BEGIN CATCH
    IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;
    THROW;
END CATCH;
GO
