-- TM-79 R3 / Option A: extend the mature canonical review and media-journal
-- parent identity without rewriting legacy review data or moderation evidence.
SET XACT_ABORT ON;
SET QUOTED_IDENTIFIER ON;
BEGIN TRY
    BEGIN TRANSACTION;

    IF OBJECT_ID(N'social.TripReviews', N'U') IS NULL
       OR OBJECT_ID(N'commerce.Bookings', N'U') IS NULL
       OR OBJECT_ID(N'commercial.ServiceBookings', N'U') IS NULL
       OR OBJECT_ID(N'catalog.POIs', N'U') IS NULL
        THROW 51000, 'TM-79 typed parents require the canonical review, booking, service-booking, and POI tables.', 1;

    DECLARE @hasServiceBooking bit = CASE WHEN COL_LENGTH(N'social.TripReviews', N'service_booking_id') IS NULL THEN 0 ELSE 1 END;
    DECLARE @hasPoi bit = CASE WHEN COL_LENGTH(N'social.TripReviews', N'poi_id') IS NULL THEN 0 ELSE 1 END;

    IF @hasServiceBooking <> @hasPoi
        THROW 51000, 'TM-79 typed review columns are only partially present; no automatic repair performed.', 1;

    IF @hasServiceBooking = 0
    BEGIN
        IF (SELECT COUNT(*) FROM sys.columns WHERE object_id = OBJECT_ID(N'social.TripReviews')) <> 18
           OR NOT EXISTS (
                SELECT 1 FROM sys.columns
                WHERE object_id = OBJECT_ID(N'social.TripReviews')
                  AND name = N'booking_id' AND system_type_id = 127 AND is_nullable = 0)
           OR NOT EXISTS (
                SELECT 1 FROM sys.indexes i
                JOIN sys.index_columns ic
                  ON ic.object_id=i.object_id AND ic.index_id=i.index_id AND ic.key_ordinal=1
                JOIN sys.columns c
                  ON c.object_id=ic.object_id AND c.column_id=ic.column_id
                WHERE i.object_id = OBJECT_ID(N'social.TripReviews')
                  AND i.name = N'UX_TripReviews_Booking' AND i.is_unique = 1
                  AND i.has_filter = 0 AND i.is_disabled = 0 AND i.ignore_dup_key = 0
                  AND c.name=N'booking_id'
                  AND (SELECT COUNT(*) FROM sys.index_columns members
                       WHERE members.object_id=i.object_id AND members.index_id=i.index_id)=1)
           OR NOT EXISTS (
                SELECT 1 FROM sys.check_constraints
                WHERE parent_object_id = OBJECT_ID(N'social.TripReviews')
                  AND name = N'CK_TripReviews_Subject' AND is_disabled = 0 AND is_not_trusted = 0
                  AND definition=N'([tour_id] IS NOT NULL AND [itinerary_id] IS NULL OR [tour_id] IS NULL AND [itinerary_id] IS NOT NULL)')
            THROW 51000, 'TM-79 legacy review shape is not the approved upgrade source.', 1;

        DROP INDEX UX_TripReviews_Booking ON social.TripReviews;
        ALTER TABLE social.TripReviews DROP CONSTRAINT CK_TripReviews_Subject;
        ALTER TABLE social.TripReviews ALTER COLUMN booking_id BIGINT NULL;
        ALTER TABLE social.TripReviews ADD service_booking_id BIGINT NULL, poi_id BIGINT NULL;
        EXEC(N'
            ALTER TABLE social.TripReviews WITH CHECK ADD CONSTRAINT FK_TripReviews_ServiceBooking
                FOREIGN KEY(service_booking_id) REFERENCES commercial.ServiceBookings(service_booking_id);
            ALTER TABLE social.TripReviews WITH CHECK ADD CONSTRAINT FK_TripReviews_Poi
                FOREIGN KEY(poi_id) REFERENCES catalog.POIs(poi_id);
            ALTER TABLE social.TripReviews WITH CHECK ADD CONSTRAINT CK_TripReviews_Parent CHECK (
                (booking_id IS NOT NULL AND service_booking_id IS NULL)
                OR (booking_id IS NULL AND service_booking_id IS NOT NULL));
            ALTER TABLE social.TripReviews WITH CHECK ADD CONSTRAINT CK_TripReviews_Subject CHECK (
                (booking_id IS NOT NULL AND service_booking_id IS NULL AND poi_id IS NULL
                    AND ((tour_id IS NOT NULL AND itinerary_id IS NULL)
                        OR (tour_id IS NULL AND itinerary_id IS NOT NULL)))
                OR
                (booking_id IS NULL AND service_booking_id IS NOT NULL
                    AND tour_id IS NULL AND itinerary_id IS NULL AND poi_id IS NOT NULL));
            CREATE UNIQUE INDEX UX_TripReviews_CommerceBooking
                ON social.TripReviews(booking_id) WHERE booking_id IS NOT NULL;
            CREATE UNIQUE INDEX UX_TripReviews_ServiceBooking
                ON social.TripReviews(service_booking_id) WHERE service_booking_id IS NOT NULL;');
    END;

    IF (SELECT COUNT(*) FROM sys.columns WHERE object_id = OBJECT_ID(N'social.TripReviews')) <> 20
       OR NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id=OBJECT_ID(N'social.TripReviews') AND name=N'booking_id' AND system_type_id=127 AND is_nullable=1)
       OR NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id=OBJECT_ID(N'social.TripReviews') AND name=N'service_booking_id' AND system_type_id=127 AND is_nullable=1)
       OR NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id=OBJECT_ID(N'social.TripReviews') AND name=N'poi_id' AND system_type_id=127 AND is_nullable=1)
       OR EXISTS (SELECT 1 FROM sys.columns WHERE object_id=OBJECT_ID(N'social.TripReviews') AND name=N'policy_version' AND is_nullable=0)
        THROW 51000, 'TM-79 typed review column inventory is invalid.', 1;

    IF EXISTS (
        SELECT required.name
        FROM (VALUES
            (N'FK_TripReviews_Booking', N'booking_id', N'commerce.Bookings', N'booking_id'),
            (N'FK_TripReviews_ServiceBooking', N'service_booking_id', N'commercial.ServiceBookings', N'service_booking_id'),
            (N'FK_TripReviews_Traveler', N'traveler_user_id', N'dbo.Users', N'user_id'),
            (N'FK_TripReviews_Tour', N'tour_id', N'commerce.Tours', N'tour_id'),
            (N'FK_TripReviews_Itinerary', N'itinerary_id', N'planning.Itineraries', N'itinerary_id'),
            (N'FK_TripReviews_Poi', N'poi_id', N'catalog.POIs', N'poi_id'))
            required(name, parent_column, referenced_object, referenced_column)
        WHERE NOT EXISTS (
            SELECT 1
            FROM sys.foreign_keys fk
            JOIN sys.foreign_key_columns fkc
              ON fkc.constraint_object_id=fk.object_id
            JOIN sys.columns parent_column
              ON parent_column.object_id=fkc.parent_object_id
             AND parent_column.column_id=fkc.parent_column_id
            JOIN sys.columns referenced_column
              ON referenced_column.object_id=fkc.referenced_object_id
             AND referenced_column.column_id=fkc.referenced_column_id
            WHERE fk.parent_object_id=OBJECT_ID(N'social.TripReviews')
              AND fk.name=required.name AND fk.is_disabled=0 AND fk.is_not_trusted=0
              AND fk.referenced_object_id=OBJECT_ID(required.referenced_object)
              AND parent_column.name=required.parent_column
              AND referenced_column.name=required.referenced_column
              AND fk.delete_referential_action=0 AND fk.update_referential_action=0
              AND (SELECT COUNT(*) FROM sys.foreign_key_columns members
                   WHERE members.constraint_object_id=fk.object_id)=1))
        THROW 51000, 'TM-79 typed review restrictive foreign keys are invalid.', 1;

    IF NOT EXISTS (
            SELECT 1 FROM sys.check_constraints
            WHERE parent_object_id=OBJECT_ID(N'social.TripReviews')
              AND name=N'CK_TripReviews_Parent' AND is_disabled=0 AND is_not_trusted=0
              AND definition=N'([booking_id] IS NOT NULL AND [service_booking_id] IS NULL OR [booking_id] IS NULL AND [service_booking_id] IS NOT NULL)')
       OR NOT EXISTS (
            SELECT 1 FROM sys.check_constraints
            WHERE parent_object_id=OBJECT_ID(N'social.TripReviews')
              AND name=N'CK_TripReviews_Subject' AND is_disabled=0 AND is_not_trusted=0
              AND definition=N'([booking_id] IS NOT NULL AND [service_booking_id] IS NULL AND [poi_id] IS NULL AND ([tour_id] IS NOT NULL AND [itinerary_id] IS NULL OR [tour_id] IS NULL AND [itinerary_id] IS NOT NULL) OR [booking_id] IS NULL AND [service_booking_id] IS NOT NULL AND [tour_id] IS NULL AND [itinerary_id] IS NULL AND [poi_id] IS NOT NULL)')
       OR NOT EXISTS (
            SELECT 1 FROM sys.indexes i
            JOIN sys.index_columns ic ON ic.object_id=i.object_id AND ic.index_id=i.index_id AND ic.key_ordinal=1
            JOIN sys.columns c ON c.object_id=ic.object_id AND c.column_id=ic.column_id
            WHERE i.object_id=OBJECT_ID(N'social.TripReviews') AND i.name=N'UX_TripReviews_CommerceBooking'
              AND i.is_unique=1 AND i.has_filter=1 AND i.filter_definition=N'([booking_id] IS NOT NULL)'
              AND i.is_disabled=0 AND i.ignore_dup_key=0 AND c.name=N'booking_id'
              AND (SELECT COUNT(*) FROM sys.index_columns members
                   WHERE members.object_id=i.object_id AND members.index_id=i.index_id)=1)
       OR NOT EXISTS (
            SELECT 1 FROM sys.indexes i
            JOIN sys.index_columns ic ON ic.object_id=i.object_id AND ic.index_id=i.index_id AND ic.key_ordinal=1
            JOIN sys.columns c ON c.object_id=ic.object_id AND c.column_id=ic.column_id
            WHERE i.object_id=OBJECT_ID(N'social.TripReviews') AND i.name=N'UX_TripReviews_ServiceBooking'
              AND i.is_unique=1 AND i.has_filter=1 AND i.filter_definition=N'([service_booking_id] IS NOT NULL)'
              AND i.is_disabled=0 AND i.ignore_dup_key=0 AND c.name=N'service_booking_id'
              AND (SELECT COUNT(*) FROM sys.index_columns members
                   WHERE members.object_id=i.object_id AND members.index_id=i.index_id)=1)
       OR EXISTS (SELECT 1 FROM sys.indexes WHERE object_id=OBJECT_ID(N'social.TripReviews') AND name=N'UX_TripReviews_Booking')
        THROW 51000, 'TM-79 typed review invariants or filtered uniqueness are invalid.', 1;

    IF OBJECT_ID(N'social.TripReviewMediaOperations', N'U') IS NOT NULL
    BEGIN
        DECLARE @mediaHasService bit = CASE WHEN COL_LENGTH(N'social.TripReviewMediaOperations', N'service_booking_id') IS NULL THEN 0 ELSE 1 END;
        IF @mediaHasService = 0
        BEGIN
            IF (SELECT COUNT(*) FROM sys.columns WHERE object_id=OBJECT_ID(N'social.TripReviewMediaOperations')) <> 20
               OR NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id=OBJECT_ID(N'social.TripReviewMediaOperations') AND name=N'booking_id' AND system_type_id=127 AND is_nullable=0)
                THROW 51000, 'TM-79 legacy media-operation shape is not the approved upgrade source.', 1;

            ALTER TABLE social.TripReviewMediaOperations ALTER COLUMN booking_id BIGINT NULL;
            ALTER TABLE social.TripReviewMediaOperations ADD service_booking_id BIGINT NULL;
            EXEC(N'
                ALTER TABLE social.TripReviewMediaOperations WITH CHECK ADD CONSTRAINT FK_TripReviewMediaOperations_ServiceBooking
                    FOREIGN KEY(service_booking_id) REFERENCES commercial.ServiceBookings(service_booking_id);
                ALTER TABLE social.TripReviewMediaOperations WITH CHECK ADD CONSTRAINT CK_TripReviewMediaOperations_Parent CHECK (
                    (booking_id IS NOT NULL AND service_booking_id IS NULL)
                    OR (booking_id IS NULL AND service_booking_id IS NOT NULL));
                CREATE INDEX IX_TripReviewMediaOperations_CommerceBooking
                    ON social.TripReviewMediaOperations(booking_id) WHERE booking_id IS NOT NULL;
                CREATE INDEX IX_TripReviewMediaOperations_ServiceBooking
                    ON social.TripReviewMediaOperations(service_booking_id) WHERE service_booking_id IS NOT NULL;');
        END;

        IF (SELECT COUNT(*) FROM sys.columns WHERE object_id=OBJECT_ID(N'social.TripReviewMediaOperations')) <> 21
           OR NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id=OBJECT_ID(N'social.TripReviewMediaOperations') AND name=N'booking_id' AND system_type_id=127 AND is_nullable=1)
           OR NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id=OBJECT_ID(N'social.TripReviewMediaOperations') AND name=N'service_booking_id' AND system_type_id=127 AND is_nullable=1)
           OR NOT EXISTS (
                SELECT 1
                FROM sys.foreign_keys fk
                JOIN sys.foreign_key_columns fkc ON fkc.constraint_object_id=fk.object_id
                JOIN sys.columns parent_column ON parent_column.object_id=fkc.parent_object_id AND parent_column.column_id=fkc.parent_column_id
                JOIN sys.columns referenced_column ON referenced_column.object_id=fkc.referenced_object_id AND referenced_column.column_id=fkc.referenced_column_id
                WHERE fk.parent_object_id=OBJECT_ID(N'social.TripReviewMediaOperations')
                  AND fk.name=N'FK_TripReviewMediaOperations_ServiceBooking'
                  AND fk.referenced_object_id=OBJECT_ID(N'commercial.ServiceBookings')
                  AND parent_column.name=N'service_booking_id' AND referenced_column.name=N'service_booking_id'
                  AND fk.is_disabled=0 AND fk.is_not_trusted=0
                  AND fk.delete_referential_action=0 AND fk.update_referential_action=0
                  AND (SELECT COUNT(*) FROM sys.foreign_key_columns members
                       WHERE members.constraint_object_id=fk.object_id)=1)
           OR NOT EXISTS (
                SELECT 1
                FROM sys.foreign_keys fk
                JOIN sys.foreign_key_columns fkc ON fkc.constraint_object_id=fk.object_id
                JOIN sys.columns parent_column ON parent_column.object_id=fkc.parent_object_id AND parent_column.column_id=fkc.parent_column_id
                JOIN sys.columns referenced_column ON referenced_column.object_id=fkc.referenced_object_id AND referenced_column.column_id=fkc.referenced_column_id
                WHERE fk.parent_object_id=OBJECT_ID(N'social.TripReviewMediaOperations')
                  AND fk.name=N'FK_TripReviewMediaOperations_Booking'
                  AND fk.referenced_object_id=OBJECT_ID(N'commerce.Bookings')
                  AND parent_column.name=N'booking_id' AND referenced_column.name=N'booking_id'
                  AND fk.is_disabled=0 AND fk.is_not_trusted=0
                  AND fk.delete_referential_action=0 AND fk.update_referential_action=0
                  AND (SELECT COUNT(*) FROM sys.foreign_key_columns members
                       WHERE members.constraint_object_id=fk.object_id)=1)
           OR NOT EXISTS (
                SELECT 1 FROM sys.check_constraints
                WHERE parent_object_id=OBJECT_ID(N'social.TripReviewMediaOperations')
                  AND name=N'CK_TripReviewMediaOperations_Parent'
                  AND is_disabled=0 AND is_not_trusted=0
                  AND definition=N'([booking_id] IS NOT NULL AND [service_booking_id] IS NULL OR [booking_id] IS NULL AND [service_booking_id] IS NOT NULL)')
           OR NOT EXISTS (
                SELECT 1 FROM sys.indexes i
                JOIN sys.index_columns ic ON ic.object_id=i.object_id AND ic.index_id=i.index_id AND ic.key_ordinal=1
                JOIN sys.columns c ON c.object_id=ic.object_id AND c.column_id=ic.column_id
                WHERE i.object_id=OBJECT_ID(N'social.TripReviewMediaOperations') AND i.name=N'IX_TripReviewMediaOperations_CommerceBooking'
                  AND i.is_unique=0 AND i.has_filter=1 AND i.filter_definition=N'([booking_id] IS NOT NULL)'
                  AND i.is_disabled=0 AND c.name=N'booking_id'
                  AND (SELECT COUNT(*) FROM sys.index_columns members
                       WHERE members.object_id=i.object_id AND members.index_id=i.index_id)=1)
           OR NOT EXISTS (
                SELECT 1 FROM sys.indexes i
                JOIN sys.index_columns ic ON ic.object_id=i.object_id AND ic.index_id=i.index_id AND ic.key_ordinal=1
                JOIN sys.columns c ON c.object_id=ic.object_id AND c.column_id=ic.column_id
                WHERE i.object_id=OBJECT_ID(N'social.TripReviewMediaOperations') AND i.name=N'IX_TripReviewMediaOperations_ServiceBooking'
                  AND i.is_unique=0 AND i.has_filter=1 AND i.filter_definition=N'([service_booking_id] IS NOT NULL)'
                  AND i.is_disabled=0 AND c.name=N'service_booking_id'
                  AND (SELECT COUNT(*) FROM sys.index_columns members
                       WHERE members.object_id=i.object_id AND members.index_id=i.index_id)=1)
            THROW 51000, 'TM-79 typed media-operation identity is invalid.', 1;
    END;

    COMMIT TRANSACTION;
END TRY
BEGIN CATCH
    IF XACT_STATE() <> 0 ROLLBACK TRANSACTION;
    THROW;
END CATCH;
