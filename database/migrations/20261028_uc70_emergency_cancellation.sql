/* UC-70: Operator-confirmed departure cancellation and emergency stop.
   Idempotent: every object is created only when missing, then its shape is checked and its
   constraints are trusted. Any mismatch raises and the whole script rolls back.
   UC-70 owns no refund, payment or payout schema. */
SET XACT_ABORT ON;
GO

BEGIN TRY
    BEGIN TRANSACTION;

    IF OBJECT_ID(N'commerce.TourSchedules', N'U') IS NULL
        THROW 51000, 'UC-70 requires commerce.TourSchedules.', 1;
    IF OBJECT_ID(N'commerce.Bookings', N'U') IS NULL
        THROW 51000, 'UC-70 requires commerce.Bookings.', 1;
    IF OBJECT_ID(N'dbo.Users', N'U') IS NULL
        THROW 51000, 'UC-70 requires dbo.Users.', 1;

    /* 1. TourSchedules.status gains EmergencyStopped. */
    IF EXISTS (
        SELECT 1 FROM sys.columns
        WHERE object_id = OBJECT_ID(N'commerce.TourSchedules')
          AND name = N'status' AND max_length < 20)
        ALTER TABLE commerce.TourSchedules ALTER COLUMN status VARCHAR(20) NOT NULL;

    DECLARE @scheduleStatusCheck sysname = (
        SELECT TOP (1) name FROM sys.check_constraints
        WHERE parent_object_id = OBJECT_ID(N'commerce.TourSchedules')
          AND definition LIKE '%[[]status]%'
          AND definition NOT LIKE '%EmergencyStopped%');
    IF @scheduleStatusCheck IS NOT NULL
    BEGIN
        DECLARE @dropScheduleCheck NVARCHAR(MAX) =
            N'ALTER TABLE commerce.TourSchedules DROP CONSTRAINT ' + QUOTENAME(@scheduleStatusCheck) + N';';
        EXEC sys.sp_executesql @dropScheduleCheck;
    END;

    IF NOT EXISTS (
        SELECT 1 FROM sys.check_constraints
        WHERE parent_object_id = OBJECT_ID(N'commerce.TourSchedules')
          AND definition LIKE '%EmergencyStopped%')
        ALTER TABLE commerce.TourSchedules WITH CHECK
            ADD CONSTRAINT CK_TourSchedules_Status
            CHECK (status IN ('Scheduled','Cancelled','Completed','EmergencyStopped'));

    ALTER TABLE commerce.TourSchedules WITH CHECK CHECK CONSTRAINT CK_TourSchedules_Status;

    /* 2. Bookings.status gains Interrupted. */
    DECLARE @bookingStatusCheck sysname = (
        SELECT TOP (1) name FROM sys.check_constraints
        WHERE parent_object_id = OBJECT_ID(N'commerce.Bookings')
          AND definition LIKE '%[[]status]%'
          AND definition NOT LIKE '%Interrupted%');
    IF @bookingStatusCheck IS NOT NULL
    BEGIN
        DECLARE @dropBookingCheck NVARCHAR(MAX) =
            N'ALTER TABLE commerce.Bookings DROP CONSTRAINT ' + QUOTENAME(@bookingStatusCheck) + N';';
        EXEC sys.sp_executesql @dropBookingCheck;
    END;

    IF NOT EXISTS (
        SELECT 1 FROM sys.check_constraints
        WHERE parent_object_id = OBJECT_ID(N'commerce.Bookings')
          AND definition LIKE '%Interrupted%')
        ALTER TABLE commerce.Bookings WITH CHECK
            ADD CONSTRAINT CK_Bookings_Status
            CHECK (status IN ('PendingPayment','Confirmed','Cancelled','Completed','Expired','Interrupted'));

    ALTER TABLE commerce.Bookings WITH CHECK CHECK CONSTRAINT CK_Bookings_Status;

    /* 3. TripSessions.completion_reason gains the UC-70 emergency reasons. */
    IF OBJECT_ID(N'trip.TripSessions', N'U') IS NOT NULL
       AND COL_LENGTH(N'trip.TripSessions', N'completion_reason') IS NOT NULL
    BEGIN
        IF EXISTS (
            SELECT 1 FROM sys.check_constraints
            WHERE parent_object_id = OBJECT_ID(N'trip.TripSessions')
              AND name = N'CK_TripSessions_CompletionReason'
              AND (definition NOT LIKE N'%SafetyIncident%' OR definition NOT LIKE N'%GroupAccessRevoked%'))
            ALTER TABLE trip.TripSessions DROP CONSTRAINT CK_TripSessions_CompletionReason;

        IF NOT EXISTS (
            SELECT 1 FROM sys.check_constraints
            WHERE parent_object_id = OBJECT_ID(N'trip.TripSessions')
              AND name = N'CK_TripSessions_CompletionReason')
            EXEC sys.sp_executesql N'
                ALTER TABLE trip.TripSessions WITH CHECK
                    ADD CONSTRAINT CK_TripSessions_CompletionReason CHECK (
                        completion_reason IS NULL
                        OR completion_reason IN (''RouteFinished'', ''TravelerStopped'', ''Expired'',
                            ''GroupAccessRevoked'', ''WeatherEmergency'', ''AuthorityDirective'', ''DestinationUnavailable'',
                            ''TransportFailure'', ''SafetyIncident'', ''Other''));';

        ALTER TABLE trip.TripSessions WITH CHECK CHECK CONSTRAINT CK_TripSessions_CompletionReason;
    END;

    /* 4. One committed decision per departure, idempotent per Operator command. */
    IF OBJECT_ID(N'commerce.EmergencyTourCancellationOperations', N'U') IS NULL
        EXEC sys.sp_executesql N'
            CREATE TABLE commerce.EmergencyTourCancellationOperations (
                operation_id BIGINT IDENTITY(1,1) NOT NULL
                    CONSTRAINT PK_EmergencyTourCancellationOperations PRIMARY KEY,
                tour_schedule_id BIGINT NOT NULL,
                actor_user_id BIGINT NOT NULL,
                command_id UNIQUEIDENTIFIER NOT NULL,
                request_hash CHAR(64) NOT NULL,
                action VARCHAR(24) NOT NULL,
                reason_category VARCHAR(40) NOT NULL,
                reason_details NVARCHAR(1000) NOT NULL,
                immediate_instructions NVARCHAR(1000) NULL,
                emergency_contact NVARCHAR(100) NULL,
                evidence_reference NVARCHAR(500) NULL,
                evidence_url NVARCHAR(1000) NULL,
                authority_reference NVARCHAR(200) NULL,
                evidence_provenance VARCHAR(20) NOT NULL
                    CONSTRAINT DF_EmergencyTourCancellationOperations_Provenance DEFAULT ''OperatorProvided'',
                evidence_added_later BIT NOT NULL
                    CONSTRAINT DF_EmergencyTourCancellationOperations_AddedLater DEFAULT 0,
                affected_booking_count INT NOT NULL,
                committed_at DATETIME2 NOT NULL,
                CONSTRAINT FK_EmergencyTourCancellationOperations_Schedule
                    FOREIGN KEY (tour_schedule_id) REFERENCES commerce.TourSchedules(schedule_id),
                CONSTRAINT FK_EmergencyTourCancellationOperations_Actor
                    FOREIGN KEY (actor_user_id) REFERENCES dbo.Users(user_id),
                CONSTRAINT CK_EmergencyTourCancellationOperations_Counts CHECK (affected_booking_count >= 0),
                CONSTRAINT UX_EmergencyTourCancellationOperations_Schedule UNIQUE (tour_schedule_id),
                CONSTRAINT UX_EmergencyTourCancellationOperations_ActorCommand UNIQUE (actor_user_id, command_id)
            );';

    IF EXISTS (
        SELECT expected.name, expected.type_name, expected.max_length, expected.is_nullable
        FROM (VALUES
            (N'operation_id', N'bigint', 8, 0),
            (N'tour_schedule_id', N'bigint', 8, 0),
            (N'actor_user_id', N'bigint', 8, 0),
            (N'command_id', N'uniqueidentifier', 16, 0),
            (N'request_hash', N'char', 64, 0),
            (N'action', N'varchar', 24, 0),
            (N'reason_category', N'varchar', 40, 0),
            (N'reason_details', N'nvarchar', 2000, 0),
            (N'immediate_instructions', N'nvarchar', 2000, 1),
            (N'emergency_contact', N'nvarchar', 200, 1),
            (N'evidence_reference', N'nvarchar', 1000, 1),
            (N'evidence_url', N'nvarchar', 2000, 1),
            (N'authority_reference', N'nvarchar', 400, 1),
            (N'evidence_provenance', N'varchar', 20, 0),
            (N'evidence_added_later', N'bit', 1, 0),
            (N'affected_booking_count', N'int', 4, 0),
            (N'committed_at', N'datetime2', 8, 0)
        ) AS expected(name, type_name, max_length, is_nullable)
        EXCEPT
        SELECT column_info.name, type_info.name, column_info.max_length, column_info.is_nullable
        FROM sys.columns AS column_info
        INNER JOIN sys.types AS type_info ON type_info.user_type_id = column_info.user_type_id
        WHERE column_info.object_id = OBJECT_ID(N'commerce.EmergencyTourCancellationOperations'))
        THROW 51000, 'UC-70 emergency operation table has an incompatible shape.', 1;

    IF (SELECT COUNT(*) FROM sys.columns
        WHERE object_id = OBJECT_ID(N'commerce.EmergencyTourCancellationOperations')) <> 17
        THROW 51000, 'UC-70 emergency operation table has unexpected columns.', 1;

    ALTER TABLE commerce.EmergencyTourCancellationOperations WITH CHECK
        CHECK CONSTRAINT FK_EmergencyTourCancellationOperations_Schedule;
    ALTER TABLE commerce.EmergencyTourCancellationOperations WITH CHECK
        CHECK CONSTRAINT FK_EmergencyTourCancellationOperations_Actor;
    ALTER TABLE commerce.EmergencyTourCancellationOperations WITH CHECK
        CHECK CONSTRAINT CK_EmergencyTourCancellationOperations_Counts;

    /* 5. The paid booking's BookedTour itinerary snapshot. */
    IF OBJECT_ID(N'planning.Itineraries', N'U') IS NOT NULL
    BEGIN
        IF COL_LENGTH(N'planning.Itineraries', N'source_booking_id') IS NULL
            EXEC sys.sp_executesql N'ALTER TABLE planning.Itineraries ADD source_booking_id BIGINT NULL;';

        IF NOT EXISTS (
            SELECT 1
            FROM sys.columns AS column_info
            INNER JOIN sys.types AS type_info ON type_info.user_type_id = column_info.user_type_id
            WHERE column_info.object_id = OBJECT_ID(N'planning.Itineraries')
              AND column_info.name = N'source_booking_id'
              AND type_info.name = N'bigint'
              AND column_info.is_nullable = 1)
            THROW 51000, 'UC-70 itinerary source_booking_id column has an incompatible shape.', 1;

        IF NOT EXISTS (SELECT 1 FROM sys.foreign_keys
            WHERE name = N'FK_Itineraries_SourceBooking'
              AND parent_object_id = OBJECT_ID(N'planning.Itineraries'))
            EXEC sys.sp_executesql N'
                ALTER TABLE planning.Itineraries WITH CHECK ADD CONSTRAINT FK_Itineraries_SourceBooking
                    FOREIGN KEY (source_booking_id) REFERENCES commerce.Bookings(booking_id);';

        ALTER TABLE planning.Itineraries WITH CHECK CHECK CONSTRAINT FK_Itineraries_SourceBooking;

        IF NOT EXISTS (SELECT 1 FROM sys.indexes
            WHERE name = N'IX_Itineraries_SourceBookingId'
              AND object_id = OBJECT_ID(N'planning.Itineraries'))
            EXEC sys.sp_executesql N'
                CREATE UNIQUE INDEX IX_Itineraries_SourceBookingId
                    ON planning.Itineraries(source_booking_id)
                    WHERE source_booking_id IS NOT NULL;';
    END;

    /* 6. The Traveler's acknowledgement of an emergency notice. */
    IF COL_LENGTH(N'commerce.Bookings', N'emergency_notice_acknowledged_at') IS NULL
        EXEC sys.sp_executesql N'ALTER TABLE commerce.Bookings ADD emergency_notice_acknowledged_at DATETIME2 NULL;';

    IF NOT EXISTS (
        SELECT 1
        FROM sys.columns AS column_info
        INNER JOIN sys.types AS type_info ON type_info.user_type_id = column_info.user_type_id
        WHERE column_info.object_id = OBJECT_ID(N'commerce.Bookings')
          AND column_info.name = N'emergency_notice_acknowledged_at'
          AND type_info.name = N'datetime2'
          AND column_info.is_nullable = 1)
        THROW 51000, 'UC-70 emergency_notice_acknowledged_at column has an incompatible shape.', 1;

    COMMIT TRANSACTION;
END TRY
BEGIN CATCH
    IF @@TRANCOUNT > 0
        ROLLBACK TRANSACTION;
    THROW;
END CATCH;
GO
