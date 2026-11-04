/* UC-70: human-confirmed departure cancellation and emergency stop.
   Idempotent migration with shape enforcement, trusted constraints, and atomic rollback on failure. */
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

    /* 1. TourSchedule.EmergencyStopped */
    /* Allow the retired automatic schema to pass this legacy shape guard so the
       later weather_operator_review migration can decommission it safely. */
    IF COL_LENGTH(N'commerce.EmergencyTourCancellationOperations', N'initiated_by_type') IS NULL
    BEGIN
        IF EXISTS (
        SELECT 1 FROM sys.columns
        WHERE object_id = OBJECT_ID(N'commerce.TourSchedules')
          AND name = N'status' AND max_length < 20)
    BEGIN
        ALTER TABLE commerce.TourSchedules ALTER COLUMN status VARCHAR(20) NOT NULL;
    END;

    DECLARE @TourScheduleStatusConstraint sysname;
    SELECT TOP (1) @TourScheduleStatusConstraint = check_constraint.name
    FROM sys.check_constraints check_constraint
    WHERE check_constraint.parent_object_id = OBJECT_ID(N'commerce.TourSchedules')
      AND check_constraint.definition LIKE '%[[]status]%'
      AND check_constraint.definition NOT LIKE '%EmergencyStopped%';

    IF @TourScheduleStatusConstraint IS NOT NULL
    BEGIN
        DECLARE @dropScheduleConstraintSql NVARCHAR(MAX) = N'ALTER TABLE commerce.TourSchedules DROP CONSTRAINT '
            + QUOTENAME(@TourScheduleStatusConstraint) + N';';
        EXEC sys.sp_executesql @dropScheduleConstraintSql;
    END;

    IF NOT EXISTS (
        SELECT 1 FROM sys.check_constraints
        WHERE parent_object_id = OBJECT_ID(N'commerce.TourSchedules')
          AND definition LIKE '%EmergencyStopped%')
    BEGIN
        ALTER TABLE commerce.TourSchedules WITH CHECK
            ADD CONSTRAINT CK_TourSchedules_Status
            CHECK (status IN ('Scheduled','Cancelled','Completed','EmergencyStopped'));
    END;

    ALTER TABLE commerce.TourSchedules WITH CHECK CHECK CONSTRAINT CK_TourSchedules_Status;

    /* 2. Booking.Interrupted */
    DECLARE @BookingStatusConstraint sysname;
    SELECT TOP (1) @BookingStatusConstraint = check_constraint.name
    FROM sys.check_constraints check_constraint
    WHERE check_constraint.parent_object_id = OBJECT_ID(N'commerce.Bookings')
      AND check_constraint.definition LIKE '%[[]status]%'
      AND check_constraint.definition NOT LIKE '%Interrupted%';

    IF @BookingStatusConstraint IS NOT NULL
    BEGIN
        DECLARE @dropBookingConstraintSql NVARCHAR(MAX) = N'ALTER TABLE commerce.Bookings DROP CONSTRAINT '
            + QUOTENAME(@BookingStatusConstraint) + N';';
        EXEC sys.sp_executesql @dropBookingConstraintSql;
    END;

    IF NOT EXISTS (
        SELECT 1 FROM sys.check_constraints
        WHERE parent_object_id = OBJECT_ID(N'commerce.Bookings')
          AND definition LIKE '%Interrupted%')
    BEGIN
        ALTER TABLE commerce.Bookings WITH CHECK
            ADD CONSTRAINT CK_Bookings_Status
            CHECK (status IN ('PendingPayment','Confirmed','Cancelled','Completed','Expired','Interrupted'));
    END;

    ALTER TABLE commerce.Bookings WITH CHECK CHECK CONSTRAINT CK_Bookings_Status;

    /* 2b. TripSession completion reason gains the UC-70 emergency stop reasons. */
    IF OBJECT_ID(N'trip.TripSessions', N'U') IS NOT NULL
    BEGIN
        DECLARE @TripSessionReasonConstraint sysname;
        SELECT TOP (1) @TripSessionReasonConstraint = check_constraint.name
        FROM sys.check_constraints check_constraint
        WHERE check_constraint.parent_object_id = OBJECT_ID(N'trip.TripSessions')
          AND check_constraint.name = N'CK_TripSessions_CompletionReason'
          AND (check_constraint.definition NOT LIKE N'%SafetyIncident%'
               OR check_constraint.definition NOT LIKE N'%GroupAccessRevoked%');

        IF @TripSessionReasonConstraint IS NOT NULL
        BEGIN
            DECLARE @dropSessionReasonSql NVARCHAR(MAX) = N'ALTER TABLE trip.TripSessions DROP CONSTRAINT '
                + QUOTENAME(@TripSessionReasonConstraint) + N';';
            EXEC sys.sp_executesql @dropSessionReasonSql;
        END;

        IF NOT EXISTS (
            SELECT 1 FROM sys.check_constraints
            WHERE parent_object_id = OBJECT_ID(N'trip.TripSessions')
              AND name = N'CK_TripSessions_CompletionReason'
              AND definition LIKE N'%SafetyIncident%'
              AND definition LIKE N'%GroupAccessRevoked%')
        BEGIN
            EXEC sys.sp_executesql N'
                ALTER TABLE trip.TripSessions WITH CHECK
                    ADD CONSTRAINT CK_TripSessions_CompletionReason CHECK (
                        completion_reason IS NULL
                        OR completion_reason IN (''RouteFinished'', ''TravelerStopped'', ''Expired'',
                            ''GroupAccessRevoked'', ''WeatherEmergency'', ''AuthorityDirective'', ''DestinationUnavailable'',
                            ''TransportFailure'', ''SafetyIncident'', ''Other''));';
        END;
    END;

    /* 3. EmergencyTourCancellationOperations */
    IF OBJECT_ID(N'commerce.EmergencyTourCancellationOperations', N'U') IS NULL
    BEGIN
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
                contract_policy_snapshot NVARCHAR(500) NOT NULL,
                affected_booking_count INT NOT NULL,
                committed_at DATETIME2 NOT NULL,
                CONSTRAINT FK_EmergencyTourCancellationOperations_Schedule
                    FOREIGN KEY (tour_schedule_id) REFERENCES commerce.TourSchedules(schedule_id),
                CONSTRAINT FK_EmergencyTourCancellationOperations_Actor
                    FOREIGN KEY (actor_user_id) REFERENCES dbo.Users(user_id),
                CONSTRAINT CK_EmergencyTourCancellationOperations_Counts CHECK (
                    affected_booking_count >= 0),
                CONSTRAINT UX_EmergencyTourCancellationOperations_Schedule UNIQUE (tour_schedule_id),
                CONSTRAINT UX_EmergencyTourCancellationOperations_ActorCommand UNIQUE (actor_user_id, command_id)
            );';
    END
    ELSE
    BEGIN
        IF COL_LENGTH(N'commerce.EmergencyTourCancellationOperations', N'evidence_url') IS NULL
            EXEC sys.sp_executesql N'ALTER TABLE commerce.EmergencyTourCancellationOperations ADD evidence_url NVARCHAR(1000) NULL;';

        IF COL_LENGTH(N'commerce.EmergencyTourCancellationOperations', N'authority_reference') IS NULL
            EXEC sys.sp_executesql N'ALTER TABLE commerce.EmergencyTourCancellationOperations ADD authority_reference NVARCHAR(200) NULL;';

        IF COL_LENGTH(N'commerce.EmergencyTourCancellationOperations', N'evidence_provenance') IS NULL
            EXEC sys.sp_executesql N'ALTER TABLE commerce.EmergencyTourCancellationOperations ADD evidence_provenance VARCHAR(20) NOT NULL
                CONSTRAINT DF_EmergencyTourCancellationOperations_Provenance DEFAULT ''OperatorProvided'';';

        IF COL_LENGTH(N'commerce.EmergencyTourCancellationOperations', N'evidence_added_later') IS NULL
            EXEC sys.sp_executesql N'ALTER TABLE commerce.EmergencyTourCancellationOperations ADD evidence_added_later BIT NOT NULL
                CONSTRAINT DF_EmergencyTourCancellationOperations_AddedLater DEFAULT 0;';
    END;

    /* 3b. emergency_contact became nullable: mandatory for an active stop only. */
    IF EXISTS (
        SELECT 1 FROM sys.columns
        WHERE object_id = OBJECT_ID(N'commerce.EmergencyTourCancellationOperations')
          AND name = N'emergency_contact' AND is_nullable = 0)
    BEGIN
        ALTER TABLE commerce.EmergencyTourCancellationOperations
            ALTER COLUMN emergency_contact NVARCHAR(100) NULL;
    END;

    IF EXISTS (
            SELECT expected.name, expected.type_name, expected.max_length, expected.precision_value,
                expected.scale_value, expected.is_nullable
            FROM (VALUES
                (N'operation_id', N'bigint', 8, 19, 0, 0),
                (N'tour_schedule_id', N'bigint', 8, 19, 0, 0),
                (N'actor_user_id', N'bigint', 8, 19, 0, 0),
                (N'command_id', N'uniqueidentifier', 16, 0, 0, 0),
                (N'request_hash', N'char', 64, 0, 0, 0),
                (N'action', N'varchar', 24, 0, 0, 0),
                (N'reason_category', N'varchar', 40, 0, 0, 0),
                (N'reason_details', N'nvarchar', 2000, 0, 0, 0),
                (N'immediate_instructions', N'nvarchar', 2000, 0, 0, 1),
                (N'emergency_contact', N'nvarchar', 200, 0, 0, 1),
                (N'evidence_reference', N'nvarchar', 1000, 0, 0, 1),
                (N'evidence_url', N'nvarchar', 2000, 0, 0, 1),
                (N'authority_reference', N'nvarchar', 400, 0, 0, 1),
                (N'evidence_provenance', N'varchar', 20, 0, 0, 0),
                (N'evidence_added_later', N'bit', 1, 1, 0, 0),
                (N'contract_policy_snapshot', N'nvarchar', 1000, 0, 0, 0),
                (N'affected_booking_count', N'int', 4, 10, 0, 0),
                (N'committed_at', N'datetime2', 8, 27, 7, 0)
            ) AS expected(name, type_name, max_length, precision_value, scale_value, is_nullable)
            EXCEPT
            SELECT column_info.name, type_info.name, column_info.max_length, column_info.precision,
                column_info.scale, column_info.is_nullable
            FROM sys.columns AS column_info
            INNER JOIN sys.types AS type_info ON type_info.user_type_id = column_info.user_type_id
            WHERE column_info.object_id = OBJECT_ID(N'commerce.EmergencyTourCancellationOperations')
        )
            THROW 51000, 'UC-70 emergency operation table has an incompatible shape.', 1;

        IF (SELECT COUNT(*) FROM sys.columns
            WHERE object_id = OBJECT_ID(N'commerce.EmergencyTourCancellationOperations')) <> 18
            THROW 51000, 'UC-70 emergency operation table has unexpected columns.', 1;
    END;

    ALTER TABLE commerce.EmergencyTourCancellationOperations WITH CHECK
        CHECK CONSTRAINT FK_EmergencyTourCancellationOperations_Schedule;
    ALTER TABLE commerce.EmergencyTourCancellationOperations WITH CHECK
        CHECK CONSTRAINT FK_EmergencyTourCancellationOperations_Actor;
    ALTER TABLE commerce.EmergencyTourCancellationOperations WITH CHECK
        CHECK CONSTRAINT CK_EmergencyTourCancellationOperations_Counts;

    /* 5. Itinerary.source_booking_id */
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
        BEGIN
            EXEC sys.sp_executesql N'
                ALTER TABLE planning.Itineraries WITH CHECK ADD CONSTRAINT FK_Itineraries_SourceBooking
                    FOREIGN KEY (source_booking_id) REFERENCES commerce.Bookings(booking_id);';
        END;

        ALTER TABLE planning.Itineraries WITH CHECK CHECK CONSTRAINT FK_Itineraries_SourceBooking;

        IF NOT EXISTS (SELECT 1 FROM sys.indexes
            WHERE name = N'IX_Itineraries_SourceBookingId'
            AND object_id = OBJECT_ID(N'planning.Itineraries'))
        BEGIN
            EXEC sys.sp_executesql N'
                CREATE UNIQUE INDEX IX_Itineraries_SourceBookingId
                    ON planning.Itineraries(source_booking_id)
                    WHERE source_booking_id IS NOT NULL;';
        END;
    END;

    /* 6. Booking.emergency_notice_acknowledged_at */
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
