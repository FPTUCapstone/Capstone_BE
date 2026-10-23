/*
    UC-28: Add order_code, payment_link_id, checkout_url, expires_at, updated_at
    to payment.PaymentTransactions. Widen gateway CHECK to include 'Mock'.
    Widen status CHECK to include 'Cancelled' and 'Expired'.
    Add unique index on order_code and filtered unique index on booking_id WHERE status = 'Pending'.
*/
SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
SET XACT_ABORT ON;
GO

BEGIN TRY
    BEGIN TRANSACTION;

    IF OBJECT_ID(N'payment.PaymentTransactions', N'U') IS NULL
        THROW 51000, 'UC-28 requires payment.PaymentTransactions.', 1;

    -- Add order_code column if missing
    IF NOT EXISTS (
        SELECT 1 FROM sys.columns
        WHERE object_id = OBJECT_ID(N'payment.PaymentTransactions')
          AND name = N'order_code'
    )
    BEGIN
        ALTER TABLE payment.PaymentTransactions
            ADD order_code BIGINT NOT NULL CONSTRAINT DF_PaymentTransactions_OrderCode DEFAULT (0);
    END;

    -- Add payment_link_id column if missing
    IF NOT EXISTS (
        SELECT 1 FROM sys.columns
        WHERE object_id = OBJECT_ID(N'payment.PaymentTransactions')
          AND name = N'payment_link_id'
    )
    BEGIN
        ALTER TABLE payment.PaymentTransactions
            ADD payment_link_id NVARCHAR(100) NULL;
    END;

    -- Add checkout_url column if missing
    IF NOT EXISTS (
        SELECT 1 FROM sys.columns
        WHERE object_id = OBJECT_ID(N'payment.PaymentTransactions')
          AND name = N'checkout_url'
    )
    BEGIN
        ALTER TABLE payment.PaymentTransactions
            ADD checkout_url NVARCHAR(1000) NULL;
    END;

    -- Add expires_at column if missing
    IF NOT EXISTS (
        SELECT 1 FROM sys.columns
        WHERE object_id = OBJECT_ID(N'payment.PaymentTransactions')
          AND name = N'expires_at'
    )
    BEGIN
        ALTER TABLE payment.PaymentTransactions
            ADD expires_at DATETIME2 NULL;
    END;

    -- Add updated_at column if missing
    IF NOT EXISTS (
        SELECT 1 FROM sys.columns
        WHERE object_id = OBJECT_ID(N'payment.PaymentTransactions')
          AND name = N'updated_at'
    )
    BEGIN
        ALTER TABLE payment.PaymentTransactions
            ADD updated_at DATETIME2 NULL;
    END;

    -- Update gateway CHECK constraint to include 'Mock'
    DECLARE @GatewayConstraint NVARCHAR(200);
    SELECT @GatewayConstraint = cc.name
    FROM sys.check_constraints cc
    JOIN sys.columns c ON cc.parent_object_id = c.object_id AND cc.parent_column_id = c.column_id
    WHERE cc.parent_object_id = OBJECT_ID(N'payment.PaymentTransactions')
      AND c.name = N'gateway';

    IF @GatewayConstraint IS NOT NULL
    BEGIN
        EXEC(N'ALTER TABLE payment.PaymentTransactions DROP CONSTRAINT [' + @GatewayConstraint + N'];');
    END;

    ALTER TABLE payment.PaymentTransactions
        ADD CONSTRAINT CK_PaymentTransactions_Gateway
        CHECK (gateway IN ('VNPAY','MoMo','PayOS','Stripe','Mock'));

    -- Update status CHECK constraint to include 'Cancelled', 'Expired'
    DECLARE @StatusConstraint NVARCHAR(200);
    SELECT @StatusConstraint = cc.name
    FROM sys.check_constraints cc
    JOIN sys.columns c ON cc.parent_object_id = c.object_id AND cc.parent_column_id = c.column_id
    WHERE cc.parent_object_id = OBJECT_ID(N'payment.PaymentTransactions')
      AND c.name = N'status';

    IF @StatusConstraint IS NOT NULL
    BEGIN
        EXEC(N'ALTER TABLE payment.PaymentTransactions DROP CONSTRAINT [' + @StatusConstraint + N'];');
    END;

    ALTER TABLE payment.PaymentTransactions
        ADD CONSTRAINT CK_PaymentTransactions_Status
        CHECK (status IN ('Pending','Success','Cancelled','Expired','Failed'));

    -- Unique index on order_code
    IF NOT EXISTS (
        SELECT 1 FROM sys.indexes
        WHERE object_id = OBJECT_ID(N'payment.PaymentTransactions')
          AND name = N'UX_PaymentTransactions_OrderCode'
    )
    BEGIN
        EXEC(N'SET QUOTED_IDENTIFIER ON; CREATE UNIQUE INDEX UX_PaymentTransactions_OrderCode
            ON payment.PaymentTransactions(order_code)
            WHERE order_code > 0;');
    END;

    -- Filtered unique index on booking_id WHERE status = 'Pending'
    IF NOT EXISTS (
        SELECT 1 FROM sys.indexes
        WHERE object_id = OBJECT_ID(N'payment.PaymentTransactions')
          AND name = N'UX_PaymentTransactions_Booking_Pending'
    )
    BEGIN
        EXEC(N'SET QUOTED_IDENTIFIER ON; CREATE UNIQUE INDEX UX_PaymentTransactions_Booking_Pending
            ON payment.PaymentTransactions(booking_id)
            WHERE status = ''Pending'';');
    END;

    COMMIT TRANSACTION;
END TRY
BEGIN CATCH
    IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;
    THROW;
END CATCH;
GO
