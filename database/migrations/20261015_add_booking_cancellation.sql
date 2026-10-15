/* UC-41: durable, transactional tour-booking cancellation. */
SET XACT_ABORT ON;
GO

BEGIN TRY
    BEGIN TRANSACTION;

    IF OBJECT_ID(N'commerce.Bookings', N'U') IS NULL
        THROW 51000, 'UC-41 requires commerce.Bookings.', 1;

    IF COL_LENGTH(N'commerce.Bookings', N'cancellation_policy_snapshot') IS NULL
        ALTER TABLE commerce.Bookings ADD cancellation_policy_snapshot NVARCHAR(500) NULL;

    UPDATE commerce.Bookings
    SET cancellation_policy_snapshot =
        N'{"atLeastSevenDaysPercent":100,"threeToSevenDaysPercent":70,"oneToThreeDaysPercent":30}'
    WHERE cancellation_policy_snapshot IS NULL;

    DECLARE @defaultName SYSNAME;
    SELECT @defaultName = dc.name
    FROM sys.default_constraints dc
    INNER JOIN sys.columns c ON c.object_id = dc.parent_object_id AND c.column_id = dc.parent_column_id
    WHERE dc.parent_object_id = OBJECT_ID(N'commerce.Bookings')
        AND c.name = N'cancellation_policy_snapshot';
    IF @defaultName IS NOT NULL EXEC(N'ALTER TABLE commerce.Bookings DROP CONSTRAINT ' + QUOTENAME(@defaultName));

    ALTER TABLE commerce.Bookings ALTER COLUMN cancellation_policy_snapshot NVARCHAR(500) NOT NULL;
    ALTER TABLE commerce.Bookings ADD CONSTRAINT DF_Bookings_CancellationPolicySnapshot DEFAULT
        (N'{"atLeastSevenDaysPercent":100,"threeToSevenDaysPercent":70,"oneToThreeDaysPercent":30}')
        FOR cancellation_policy_snapshot;

    IF OBJECT_ID(N'commerce.BookingCancellationOperations', N'U') IS NULL
    BEGIN
        CREATE TABLE commerce.BookingCancellationOperations (
            operation_id BIGINT IDENTITY(1,1) PRIMARY KEY,
            booking_id BIGINT NOT NULL REFERENCES commerce.Bookings(booking_id),
            actor_user_id BIGINT NOT NULL REFERENCES dbo.Users(user_id),
            actor VARCHAR(12) NOT NULL CHECK (actor IN ('Traveler','TourOperator')),
            idempotency_key UNIQUEIDENTIFIER NOT NULL,
            request_hash CHAR(64) NOT NULL,
            refund_amount DECIMAL(12,2) NOT NULL CHECK (refund_amount >= 0),
            cancelled_at DATETIME2 NOT NULL,
            CONSTRAINT UX_BookingCancellationOperations_ActorKey UNIQUE (actor_user_id, idempotency_key)
        );
    END;

    IF COL_LENGTH(N'commerce.BookingCancellationOperations', N'booking_id') IS NULL
        OR COL_LENGTH(N'commerce.BookingCancellationOperations', N'actor_user_id') IS NULL
        OR COL_LENGTH(N'commerce.BookingCancellationOperations', N'actor') IS NULL
        OR COL_LENGTH(N'commerce.BookingCancellationOperations', N'idempotency_key') IS NULL
        OR COL_LENGTH(N'commerce.BookingCancellationOperations', N'request_hash') IS NULL
        OR COL_LENGTH(N'commerce.BookingCancellationOperations', N'refund_amount') IS NULL
        OR COL_LENGTH(N'commerce.BookingCancellationOperations', N'cancelled_at') IS NULL
        THROW 51000, 'UC-41 cancellation operation table has an incompatible shape.', 1;

    IF EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'commerce.BookingCancellationOperations')
        AND name = N'IX_BookingCancellationOperations_Booking')
        DROP INDEX IX_BookingCancellationOperations_Booking ON commerce.BookingCancellationOperations;
    CREATE INDEX IX_BookingCancellationOperations_Booking ON commerce.BookingCancellationOperations(booking_id);

    IF EXISTS (SELECT 1 FROM sys.key_constraints WHERE parent_object_id = OBJECT_ID(N'commerce.BookingCancellationOperations')
        AND name = N'UX_BookingCancellationOperations_ActorKey')
        ALTER TABLE commerce.BookingCancellationOperations
            DROP CONSTRAINT UX_BookingCancellationOperations_ActorKey;
    ELSE IF EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'commerce.BookingCancellationOperations')
        AND name = N'UX_BookingCancellationOperations_ActorKey')
        DROP INDEX UX_BookingCancellationOperations_ActorKey ON commerce.BookingCancellationOperations;
    ALTER TABLE commerce.BookingCancellationOperations
        ADD CONSTRAINT UX_BookingCancellationOperations_ActorKey UNIQUE (actor_user_id, idempotency_key);

    IF COL_LENGTH(N'payment.Refunds', N'cancellation_operation_id') IS NULL
        ALTER TABLE payment.Refunds ADD cancellation_operation_id BIGINT NULL;

    IF EXISTS (SELECT 1 FROM sys.foreign_keys WHERE parent_object_id = OBJECT_ID(N'payment.Refunds')
        AND name = N'FK_Refunds_CancellationOperation')
        ALTER TABLE payment.Refunds DROP CONSTRAINT FK_Refunds_CancellationOperation;
    ALTER TABLE payment.Refunds ADD CONSTRAINT FK_Refunds_CancellationOperation
        FOREIGN KEY (cancellation_operation_id)
        REFERENCES commerce.BookingCancellationOperations(operation_id);

    IF EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'payment.Refunds')
        AND name = N'UX_Refunds_CancellationOperation')
        DROP INDEX UX_Refunds_CancellationOperation ON payment.Refunds;
    CREATE UNIQUE INDEX UX_Refunds_CancellationOperation ON payment.Refunds(cancellation_operation_id)
        WHERE cancellation_operation_id IS NOT NULL;

    COMMIT TRANSACTION;
END TRY
BEGIN CATCH
    IF XACT_STATE() <> 0 ROLLBACK TRANSACTION;
    THROW;
END CATCH;
GO
