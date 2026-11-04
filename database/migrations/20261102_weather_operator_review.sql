/* Weather ingestion for Operator review only.
   This migration removes the retired UC-70 automatic-cancellation schema and
   keeps weather inbox, normalized evidence, evaluations, reviews, and locations.
   It refuses to discard existing automatic-cancellation data silently. */
SET XACT_ABORT ON;
GO

BEGIN TRY
    BEGIN TRANSACTION;

    IF OBJECT_ID(N'commerce.TourSchedules', N'U') IS NULL
        THROW 51000, 'Weather Operator review requires commerce.TourSchedules.', 1;

    IF SCHEMA_ID(N'trip') IS NULL
        EXEC sys.sp_executesql N'CREATE SCHEMA trip;';

    IF OBJECT_ID(N'trip.WeatherEventEvaluations', N'U') IS NOT NULL
    BEGIN
        IF EXISTS (SELECT 1 FROM sys.foreign_keys WHERE parent_object_id = OBJECT_ID(N'trip.WeatherEventEvaluations') AND name = N'FK_WeatherEventEvaluations_Operation')
            ALTER TABLE trip.WeatherEventEvaluations DROP CONSTRAINT FK_WeatherEventEvaluations_Operation;
        IF COL_LENGTH(N'trip.WeatherEventEvaluations', N'emergency_cancellation_operation_id') IS NOT NULL
            EXEC sys.sp_executesql N'ALTER TABLE trip.WeatherEventEvaluations DROP COLUMN emergency_cancellation_operation_id;';
        IF COL_LENGTH(N'trip.WeatherEventEvaluations', N'mode') IS NOT NULL
            EXEC sys.sp_executesql N'ALTER TABLE trip.WeatherEventEvaluations DROP COLUMN mode;';
    END;

    IF OBJECT_ID(N'commerce.EmergencyTourCancellationOperations', N'U') IS NOT NULL
       AND COL_LENGTH(N'commerce.EmergencyTourCancellationOperations', N'initiated_by_type') IS NOT NULL
    BEGIN
        EXEC sys.sp_executesql N'
            IF EXISTS (SELECT 1 FROM commerce.EmergencyTourCancellationOperations WHERE actor_user_id IS NULL OR initiated_by_type <> ''Operator'')
                THROW 51000, ''System-initiated UC-70 operations exist; reconcile them before removing automatic cancellation.'', 1;

            IF EXISTS (SELECT 1 FROM sys.check_constraints WHERE parent_object_id = OBJECT_ID(N''commerce.EmergencyTourCancellationOperations'') AND name = N''CK_EmergencyTourCancellationOperations_Initiator'')
                ALTER TABLE commerce.EmergencyTourCancellationOperations DROP CONSTRAINT CK_EmergencyTourCancellationOperations_Initiator;
            IF EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N''commerce.EmergencyTourCancellationOperations'') AND name = N''UX_EmergencyTourCancellationOperations_WeatherEventCommand'')
                DROP INDEX UX_EmergencyTourCancellationOperations_WeatherEventCommand ON commerce.EmergencyTourCancellationOperations;
            IF EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N''commerce.EmergencyTourCancellationOperations'') AND name = N''UX_EmergencyTourCancellationOperations_ActorCommand'')
                DROP INDEX UX_EmergencyTourCancellationOperations_ActorCommand ON commerce.EmergencyTourCancellationOperations;
            IF EXISTS (SELECT 1 FROM sys.default_constraints WHERE parent_object_id = OBJECT_ID(N''commerce.EmergencyTourCancellationOperations'') AND name = N''DF_EmergencyTourCancellationOperations_Initiator'')
                ALTER TABLE commerce.EmergencyTourCancellationOperations DROP CONSTRAINT DF_EmergencyTourCancellationOperations_Initiator;
            IF EXISTS (SELECT 1 FROM sys.default_constraints WHERE parent_object_id = OBJECT_ID(N''commerce.EmergencyTourCancellationOperations'') AND name = N''DF_EmergencyTourCancellationOperations_Trigger'')
                ALTER TABLE commerce.EmergencyTourCancellationOperations DROP CONSTRAINT DF_EmergencyTourCancellationOperations_Trigger;

            ALTER TABLE commerce.EmergencyTourCancellationOperations DROP COLUMN
                initiated_by_type, trigger_type, weather_provider, weather_event_id,
                weather_revision, policy_version, decision_reason, evaluated_at;
            ALTER TABLE commerce.EmergencyTourCancellationOperations ALTER COLUMN actor_user_id BIGINT NOT NULL;
            CREATE UNIQUE NONCLUSTERED INDEX UX_EmergencyTourCancellationOperations_ActorCommand
                ON commerce.EmergencyTourCancellationOperations (actor_user_id, command_id);
        ';
    END;

    IF OBJECT_ID(N'trip.WeatherEvents', N'U') IS NULL
    BEGIN
        CREATE TABLE trip.WeatherEvents (
            weather_event_id BIGINT IDENTITY(1,1) PRIMARY KEY,
            region_name NVARCHAR(150) NULL,
            latitude DECIMAL(9,6) NULL,
            longitude DECIMAL(9,6) NULL,
            event_type VARCHAR(40) NOT NULL,
            severity VARCHAR(10) NOT NULL CHECK (severity IN ('Low','Moderate','Severe','Extreme')),
            description NVARCHAR(1000) NULL,
            valid_from DATETIME2 NOT NULL,
            valid_to DATETIME2 NULL,
            source VARCHAR(50) NOT NULL DEFAULT 'WeatherAPI',
            ingested_at DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME()
        );
    END;

    IF OBJECT_ID(N'trip.WeatherAlertInboxItems', N'U') IS NULL
    BEGIN
        CREATE TABLE trip.WeatherAlertInboxItems (
            inbox_id BIGINT IDENTITY(1,1) NOT NULL CONSTRAINT PK_WeatherAlertInboxItems PRIMARY KEY,
            provider VARCHAR(50) NOT NULL,
            provider_notification_id VARCHAR(150) NULL,
            payload NVARCHAR(MAX) NOT NULL,
            signature VARCHAR(200) NULL,
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
    END;

    IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'trip.WeatherAlertInboxItems') AND name = N'UX_WeatherAlertInboxItems_ProviderNotification')
        CREATE UNIQUE NONCLUSTERED INDEX UX_WeatherAlertInboxItems_ProviderNotification
            ON trip.WeatherAlertInboxItems (provider, provider_notification_id) WHERE provider_notification_id IS NOT NULL;
    IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'trip.WeatherAlertInboxItems') AND name = N'IX_WeatherAlertInboxItems_StatusLease')
        CREATE NONCLUSTERED INDEX IX_WeatherAlertInboxItems_StatusLease
            ON trip.WeatherAlertInboxItems (status, lease_expires_at);

    IF COL_LENGTH(N'trip.WeatherEvents', N'provider') IS NULL ALTER TABLE trip.WeatherEvents ADD provider VARCHAR(50) NULL;
    IF COL_LENGTH(N'trip.WeatherEvents', N'provider_event_id') IS NULL ALTER TABLE trip.WeatherEvents ADD provider_event_id VARCHAR(150) NULL;
    IF COL_LENGTH(N'trip.WeatherEvents', N'revision') IS NULL ALTER TABLE trip.WeatherEvents ADD revision VARCHAR(50) NULL;
    IF COL_LENGTH(N'trip.WeatherEvents', N'original_authority') IS NULL ALTER TABLE trip.WeatherEvents ADD original_authority NVARCHAR(200) NULL;
    IF COL_LENGTH(N'trip.WeatherEvents', N'original_source_reference') IS NULL ALTER TABLE trip.WeatherEvents ADD original_source_reference NVARCHAR(500) NULL;
    IF COL_LENGTH(N'trip.WeatherEvents', N'official_vietnam_risk_level') IS NULL ALTER TABLE trip.WeatherEvents ADD official_vietnam_risk_level INT NULL;
    IF COL_LENGTH(N'trip.WeatherEvents', N'verified_directive') IS NULL ALTER TABLE trip.WeatherEvents ADD verified_directive VARCHAR(50) NULL;
    IF COL_LENGTH(N'trip.WeatherEvents', N'is_withdrawn') IS NULL ALTER TABLE trip.WeatherEvents ADD is_withdrawn BIT NOT NULL CONSTRAINT DF_WeatherEvents_IsWithdrawn DEFAULT 0;
    IF COL_LENGTH(N'trip.WeatherEvents', N'geometry_geojson') IS NULL ALTER TABLE trip.WeatherEvents ADD geometry_geojson NVARCHAR(MAX) NULL;
    IF COL_LENGTH(N'trip.WeatherEvents', N'raw_payload_hash') IS NULL ALTER TABLE trip.WeatherEvents ADD raw_payload_hash VARCHAR(64) NULL;
    IF COL_LENGTH(N'trip.WeatherEvents', N'updated_at') IS NULL ALTER TABLE trip.WeatherEvents ADD updated_at DATETIME2 NULL;

    IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'trip.WeatherEvents') AND name = N'UX_WeatherEvents_ProviderEvent')
        EXEC sys.sp_executesql N'CREATE UNIQUE NONCLUSTERED INDEX UX_WeatherEvents_ProviderEvent ON trip.WeatherEvents (provider, provider_event_id)
            WHERE provider IS NOT NULL AND provider_event_id IS NOT NULL;';
    IF NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE parent_object_id = OBJECT_ID(N'trip.WeatherEvents') AND name = N'CK_WeatherEvents_RiskLevel')
        EXEC sys.sp_executesql N'ALTER TABLE trip.WeatherEvents WITH CHECK ADD CONSTRAINT CK_WeatherEvents_RiskLevel
            CHECK (official_vietnam_risk_level IS NULL OR official_vietnam_risk_level BETWEEN 1 AND 5);';

    IF OBJECT_ID(N'trip.WeatherEventEvaluations', N'U') IS NULL
    BEGIN
        CREATE TABLE trip.WeatherEventEvaluations (
            evaluation_id BIGINT IDENTITY(1,1) NOT NULL CONSTRAINT PK_WeatherEventEvaluations PRIMARY KEY,
            provider VARCHAR(50) NOT NULL,
            provider_event_id VARCHAR(150) NOT NULL,
            tour_schedule_id BIGINT NOT NULL CONSTRAINT FK_WeatherEventEvaluations_TourSchedule REFERENCES commerce.TourSchedules(schedule_id),
            decision_outcome VARCHAR(30) NOT NULL,
            reason_code VARCHAR(100) NOT NULL,
            reason_details NVARCHAR(1000) NULL,
            evaluated_at DATETIME2 NOT NULL
        );
    END;
    IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'trip.WeatherEventEvaluations') AND name = N'UX_WeatherEventEvaluations_ProviderEventSchedule')
        CREATE UNIQUE NONCLUSTERED INDEX UX_WeatherEventEvaluations_ProviderEventSchedule
            ON trip.WeatherEventEvaluations (provider, provider_event_id, tour_schedule_id);

    IF OBJECT_ID(N'trip.WeatherSafetyReviews', N'U') IS NULL
    BEGIN
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
    END;
    IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'trip.WeatherSafetyReviews') AND name = N'UX_WeatherSafetyReviews_ScheduleEvent')
        CREATE UNIQUE NONCLUSTERED INDEX UX_WeatherSafetyReviews_ScheduleEvent
            ON trip.WeatherSafetyReviews (tour_schedule_id, provider, provider_event_id);

    IF OBJECT_ID(N'trip.WeatherLocationMappings', N'U') IS NULL
    BEGIN
        CREATE TABLE trip.WeatherLocationMappings (
            location_mapping_id BIGINT IDENTITY(1,1) NOT NULL CONSTRAINT PK_WeatherLocationMappings PRIMARY KEY,
            trip_mate_location_key VARCHAR(100) NOT NULL,
            tomorrow_location_id VARCHAR(100) NOT NULL,
            latitude DECIMAL(9,6) NOT NULL,
            longitude DECIMAL(9,6) NOT NULL,
            name NVARCHAR(200) NULL,
            linked_departures_count INT NOT NULL CONSTRAINT DF_WeatherLocationMappings_LinkedCount DEFAULT 0,
            last_departure_end_at DATETIME2 NULL,
            is_active BIT NOT NULL CONSTRAINT DF_WeatherLocationMappings_IsActive DEFAULT 1,
            created_at DATETIME2 NOT NULL,
            updated_at DATETIME2 NOT NULL,
            CONSTRAINT CK_WeatherLocationMappings_Coordinates CHECK (latitude BETWEEN -90 AND 90 AND longitude BETWEEN -180 AND 180),
            CONSTRAINT CK_WeatherLocationMappings_Count CHECK (linked_departures_count >= 0)
        );
    END;
    IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'trip.WeatherLocationMappings') AND name = N'UX_WeatherLocationMappings_Key')
        CREATE UNIQUE NONCLUSTERED INDEX UX_WeatherLocationMappings_Key ON trip.WeatherLocationMappings (trip_mate_location_key);
    IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'trip.WeatherLocationMappings') AND name = N'UX_WeatherLocationMappings_TomorrowId')
        CREATE UNIQUE NONCLUSTERED INDEX UX_WeatherLocationMappings_TomorrowId ON trip.WeatherLocationMappings (tomorrow_location_id);

    ALTER TABLE trip.WeatherAlertInboxItems WITH CHECK CHECK CONSTRAINT ALL;
    ALTER TABLE trip.WeatherEvents WITH CHECK CHECK CONSTRAINT ALL;
    ALTER TABLE trip.WeatherEventEvaluations WITH CHECK CHECK CONSTRAINT ALL;
    ALTER TABLE trip.WeatherSafetyReviews WITH CHECK CHECK CONSTRAINT ALL;
    ALTER TABLE trip.WeatherLocationMappings WITH CHECK CHECK CONSTRAINT ALL;

    COMMIT TRANSACTION;
END TRY
BEGIN CATCH
    IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;
    THROW;
END CATCH;
GO
