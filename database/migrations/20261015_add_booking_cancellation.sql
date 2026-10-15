/* UC-41: durable, transactional tour-booking cancellation. */
SET XACT_ABORT ON;
GO

BEGIN TRY
    BEGIN TRANSACTION;

    IF OBJECT_ID(N'commerce.Bookings', N'U') IS NULL
        THROW 51000, 'UC-41 requires commerce.Bookings.', 1;

    IF COL_LENGTH(N'commerce.Bookings', N'cancellation_policy_snapshot') IS NULL
        EXEC sys.sp_executesql N'ALTER TABLE commerce.Bookings ADD cancellation_policy_snapshot NVARCHAR(500) NULL;';

    /* A same-name legacy column is not enough: this UC owns NVARCHAR(500). Nullability is
       repaired below after existing rows are backfilled. */
    IF NOT EXISTS (
        SELECT 1
        FROM sys.columns AS column_info
        INNER JOIN sys.types AS type_info ON type_info.user_type_id = column_info.user_type_id
        WHERE column_info.object_id = OBJECT_ID(N'commerce.Bookings')
          AND column_info.name = N'cancellation_policy_snapshot'
          AND type_info.name = N'nvarchar'
          AND column_info.max_length = 1000)
        THROW 51000, 'UC-41 booking cancellation policy snapshot column has an incompatible shape.', 1;

    EXEC sys.sp_executesql N'
        UPDATE commerce.Bookings
        SET cancellation_policy_snapshot =
            N''{"atLeastSevenDaysPercent":100,"threeToSevenDaysPercent":70,"oneToThreeDaysPercent":30}''
        WHERE cancellation_policy_snapshot IS NULL;';

    DECLARE @defaultName SYSNAME;
    SELECT @defaultName = dc.name
    FROM sys.default_constraints dc
    INNER JOIN sys.columns c ON c.object_id = dc.parent_object_id AND c.column_id = dc.parent_column_id
    WHERE dc.parent_object_id = OBJECT_ID(N'commerce.Bookings')
        AND c.name = N'cancellation_policy_snapshot';
    IF @defaultName IS NOT NULL
    BEGIN
        DECLARE @dropDefaultSql NVARCHAR(MAX) = N'ALTER TABLE commerce.Bookings DROP CONSTRAINT ' + QUOTENAME(@defaultName) + N';';
        EXEC sys.sp_executesql @dropDefaultSql;
    END;

    EXEC sys.sp_executesql N'
        ALTER TABLE commerce.Bookings ALTER COLUMN cancellation_policy_snapshot NVARCHAR(500) NOT NULL;
        ALTER TABLE commerce.Bookings ADD CONSTRAINT DF_Bookings_CancellationPolicySnapshot DEFAULT
            (N''{"atLeastSevenDaysPercent":100,"threeToSevenDaysPercent":70,"oneToThreeDaysPercent":30}'')
            FOR cancellation_policy_snapshot;';

    IF OBJECT_ID(N'commerce.BookingCancellationOperations', N'U') IS NULL
    BEGIN
        EXEC sys.sp_executesql N'
            CREATE TABLE commerce.BookingCancellationOperations (
                operation_id BIGINT IDENTITY(1,1) NOT NULL
                    CONSTRAINT PK_BookingCancellationOperations PRIMARY KEY,
                booking_id BIGINT NOT NULL,
                actor_user_id BIGINT NOT NULL,
                actor VARCHAR(12) NOT NULL,
                idempotency_key UNIQUEIDENTIFIER NOT NULL,
                request_hash CHAR(64) NOT NULL,
                refund_amount DECIMAL(12,2) NOT NULL,
                cancelled_at DATETIME2 NOT NULL,
                CONSTRAINT UX_BookingCancellationOperations_ActorKey UNIQUE (actor_user_id, idempotency_key),
                CONSTRAINT FK_BookingCancellationOperations_Booking FOREIGN KEY (booking_id)
                    REFERENCES commerce.Bookings(booking_id),
                CONSTRAINT FK_BookingCancellationOperations_ActorUser FOREIGN KEY (actor_user_id)
                    REFERENCES dbo.Users(user_id),
                CONSTRAINT CK_BookingCancellationOperations_Actor
                    CHECK (actor IN (''Traveler'',''TourOperator'')),
                CONSTRAINT CK_BookingCancellationOperations_RefundAmount
                    CHECK (refund_amount >= 0)
            );';
    END;

    IF EXISTS (
        SELECT expected.name, expected.type_name, expected.max_length, expected.precision_value,
            expected.scale_value, expected.is_nullable, expected.is_identity
        FROM (VALUES
            (N'operation_id', N'bigint', 8, 19, 0, 0, 1),
            (N'booking_id', N'bigint', 8, 19, 0, 0, 0),
            (N'actor_user_id', N'bigint', 8, 19, 0, 0, 0),
            (N'actor', N'varchar', 12, 0, 0, 0, 0),
            (N'idempotency_key', N'uniqueidentifier', 16, 0, 0, 0, 0),
            (N'request_hash', N'char', 64, 0, 0, 0, 0),
            (N'refund_amount', N'decimal', 9, 12, 2, 0, 0),
            (N'cancelled_at', N'datetime2', 8, 27, 7, 0, 0)
        ) AS expected(name, type_name, max_length, precision_value, scale_value, is_nullable, is_identity)
        EXCEPT
        SELECT column_info.name, type_info.name, column_info.max_length, column_info.precision,
            column_info.scale, column_info.is_nullable, column_info.is_identity
        FROM sys.columns AS column_info
        INNER JOIN sys.types AS type_info ON type_info.user_type_id = column_info.user_type_id
        WHERE column_info.object_id = OBJECT_ID(N'commerce.BookingCancellationOperations')
    )
        THROW 51000, 'UC-41 cancellation operation table has an incompatible shape.', 1;

    IF (SELECT COUNT(*) FROM sys.columns
        WHERE object_id = OBJECT_ID(N'commerce.BookingCancellationOperations')) <> 8
        THROW 51000, 'UC-41 cancellation operation table has unexpected columns.', 1;

    IF NOT EXISTS (
        SELECT 1
        FROM sys.key_constraints AS key_info
        WHERE key_info.parent_object_id = OBJECT_ID(N'commerce.BookingCancellationOperations')
          AND key_info.type = N'PK'
          AND INDEX_COL(N'commerce.BookingCancellationOperations', key_info.unique_index_id, 1) = N'operation_id'
          AND INDEX_COL(N'commerce.BookingCancellationOperations', key_info.unique_index_id, 2) IS NULL)
        THROW 51000, 'UC-41 cancellation operation primary key has an incompatible shape.', 1;

    DECLARE @dropOperationConstraints NVARCHAR(MAX) = N'';
    SELECT @dropOperationConstraints += N'ALTER TABLE commerce.BookingCancellationOperations DROP CONSTRAINT '
        + QUOTENAME(constraint_info.name) + N';'
    FROM (
        SELECT foreign_key.name
        FROM sys.foreign_keys AS foreign_key
        INNER JOIN sys.foreign_key_columns AS column_map
            ON column_map.constraint_object_id = foreign_key.object_id
        WHERE foreign_key.parent_object_id = OBJECT_ID(N'commerce.BookingCancellationOperations')
          AND column_map.parent_column_id IN (
              COLUMNPROPERTY(OBJECT_ID(N'commerce.BookingCancellationOperations'), N'booking_id', N'ColumnId'),
              COLUMNPROPERTY(OBJECT_ID(N'commerce.BookingCancellationOperations'), N'actor_user_id', N'ColumnId'))
        UNION
        SELECT check_info.name
        FROM sys.check_constraints AS check_info
        WHERE check_info.parent_object_id = OBJECT_ID(N'commerce.BookingCancellationOperations')
          AND (check_info.name IN (
                N'CK_BookingCancellationOperations_Actor',
                N'CK_BookingCancellationOperations_RefundAmount')
              OR check_info.parent_column_id IN (
                COLUMNPROPERTY(OBJECT_ID(N'commerce.BookingCancellationOperations'), N'actor', N'ColumnId'),
                COLUMNPROPERTY(OBJECT_ID(N'commerce.BookingCancellationOperations'), N'refund_amount', N'ColumnId')))
    ) AS constraint_info;
    IF @dropOperationConstraints <> N'' EXEC sys.sp_executesql @dropOperationConstraints;

    EXEC sys.sp_executesql N'
        ALTER TABLE commerce.BookingCancellationOperations
            ADD CONSTRAINT FK_BookingCancellationOperations_Booking FOREIGN KEY (booking_id)
                REFERENCES commerce.Bookings(booking_id),
                CONSTRAINT FK_BookingCancellationOperations_ActorUser FOREIGN KEY (actor_user_id)
                REFERENCES dbo.Users(user_id),
                CONSTRAINT CK_BookingCancellationOperations_Actor
                CHECK (actor IN (''Traveler'',''TourOperator'')),
                CONSTRAINT CK_BookingCancellationOperations_RefundAmount
                CHECK (refund_amount >= 0);';

    IF EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'commerce.BookingCancellationOperations')
        AND name = N'IX_BookingCancellationOperations_Booking')
        DROP INDEX IX_BookingCancellationOperations_Booking ON commerce.BookingCancellationOperations;
    EXEC sys.sp_executesql N'CREATE INDEX IX_BookingCancellationOperations_Booking ON commerce.BookingCancellationOperations(booking_id);';

    IF EXISTS (SELECT 1 FROM sys.key_constraints WHERE parent_object_id = OBJECT_ID(N'commerce.BookingCancellationOperations')
        AND name = N'UX_BookingCancellationOperations_ActorKey')
        ALTER TABLE commerce.BookingCancellationOperations
            DROP CONSTRAINT UX_BookingCancellationOperations_ActorKey;
    ELSE IF EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'commerce.BookingCancellationOperations')
        AND name = N'UX_BookingCancellationOperations_ActorKey')
        DROP INDEX UX_BookingCancellationOperations_ActorKey ON commerce.BookingCancellationOperations;
    EXEC sys.sp_executesql N'ALTER TABLE commerce.BookingCancellationOperations
        ADD CONSTRAINT UX_BookingCancellationOperations_ActorKey UNIQUE (actor_user_id, idempotency_key);';

    IF COL_LENGTH(N'payment.Refunds', N'cancellation_operation_id') IS NULL
        EXEC sys.sp_executesql N'ALTER TABLE payment.Refunds ADD cancellation_operation_id BIGINT NULL;';

    DECLARE @dropRefundForeignKeys NVARCHAR(MAX) = N'';
    SELECT @dropRefundForeignKeys += N'ALTER TABLE payment.Refunds DROP CONSTRAINT '
        + QUOTENAME(foreign_key.name) + N';'
    FROM sys.foreign_keys AS foreign_key
    INNER JOIN sys.foreign_key_columns AS column_map
        ON column_map.constraint_object_id = foreign_key.object_id
    WHERE foreign_key.parent_object_id = OBJECT_ID(N'payment.Refunds')
      AND column_map.parent_column_id = COLUMNPROPERTY(
          OBJECT_ID(N'payment.Refunds'), N'cancellation_operation_id', N'ColumnId');
    IF @dropRefundForeignKeys <> N'' EXEC sys.sp_executesql @dropRefundForeignKeys;

    EXEC sys.sp_executesql N'ALTER TABLE payment.Refunds ADD CONSTRAINT FK_Refunds_CancellationOperation
        FOREIGN KEY (cancellation_operation_id)
        REFERENCES commerce.BookingCancellationOperations(operation_id);';

    IF EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'payment.Refunds')
        AND name = N'UX_Refunds_CancellationOperation')
        DROP INDEX UX_Refunds_CancellationOperation ON payment.Refunds;
    EXEC sys.sp_executesql N'CREATE UNIQUE INDEX UX_Refunds_CancellationOperation ON payment.Refunds(cancellation_operation_id)
        WHERE cancellation_operation_id IS NOT NULL;';

    COMMIT TRANSACTION;
END TRY
BEGIN CATCH
    IF XACT_STATE() <> 0 ROLLBACK TRANSACTION;
    THROW;
END CATCH;
GO
