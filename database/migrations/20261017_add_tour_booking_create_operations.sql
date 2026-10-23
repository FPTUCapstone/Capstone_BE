/*
    UC-27: durable idempotency record for the Book Tour create-booking request.
    Additive/idempotent SQL Server migration. commerce.Bookings, commerce.BookingParticipants,
    commerce.Vouchers and commerce.VoucherRedemptions already exist (tripmate_schema_v7.sql) and
    are reused as-is; this migration only adds the new idempotency table, following the same
    pattern as commerce.TourPackageCreateOperations.
*/
SET XACT_ABORT ON;
GO

BEGIN TRY
    BEGIN TRANSACTION;

    IF OBJECT_ID(N'commerce.Bookings', N'U') IS NULL
        THROW 51000, 'UC-27 requires commerce.Bookings.', 1;
    IF OBJECT_ID(N'dbo.Users', N'U') IS NULL
        THROW 51000, 'UC-27 requires dbo.Users.', 1;

    IF OBJECT_ID(N'commerce.TourBookingCreateOperations', N'U') IS NULL
    BEGIN
        CREATE TABLE commerce.TourBookingCreateOperations (
            create_operation_id BIGINT IDENTITY(1,1) NOT NULL
                CONSTRAINT PK_TourBookingCreateOperations PRIMARY KEY,
            traveler_user_id BIGINT NOT NULL,
            idempotency_key UNIQUEIDENTIFIER NOT NULL,
            payload_fingerprint CHAR(64) COLLATE Latin1_General_100_BIN2 NOT NULL,
            booking_id BIGINT NULL,
            created_at DATETIME2 NOT NULL
                CONSTRAINT DF_TourBookingCreateOperations_CreatedAt DEFAULT SYSUTCDATETIME(),
            completed_at DATETIME2 NULL,
            CONSTRAINT FK_TourBookingCreateOperations_Traveler FOREIGN KEY (traveler_user_id)
                REFERENCES dbo.Users(user_id),
            CONSTRAINT FK_TourBookingCreateOperations_Booking FOREIGN KEY (booking_id)
                REFERENCES commerce.Bookings(booking_id),
            CONSTRAINT CK_TourBookingCreateOperations_Fingerprint CHECK
                (LEN(payload_fingerprint) = 64 AND payload_fingerprint NOT LIKE '%[^0-9A-F]%' COLLATE Latin1_General_100_BIN2),
            CONSTRAINT UQ_TourBookingCreateOperations_TravelerKey UNIQUE (traveler_user_id, idempotency_key)
        );
    END;

    COMMIT TRANSACTION;
END TRY
BEGIN CATCH
    IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;
    THROW;
END CATCH;
GO
