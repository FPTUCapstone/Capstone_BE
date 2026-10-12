/*
    TM-208: Payout lifecycle foundation for UC-63/64/65/73/74.
    This migration is intentionally non-destructive: it requires payment.Payouts
    to be empty. Local/dev/test fixtures must be reset before applying it.
*/
SET XACT_ABORT ON;
GO

BEGIN TRY
    BEGIN TRANSACTION;

    IF OBJECT_ID(N'payment.Payouts', N'U') IS NULL
        THROW 51000, 'TM-208 requires payment.Payouts.', 1;
    IF OBJECT_ID(N'dbo.Users', N'U') IS NULL
        THROW 51000, 'TM-208 requires dbo.Users.', 1;

    IF EXISTS (SELECT 1 FROM payment.Payouts)
        THROW 51000, 'TM-208 requires payment.Payouts to be reset before migration; no payout data is transformed automatically.', 1;

    DECLARE @sql NVARCHAR(MAX) = N'';

    SELECT @sql += N'ALTER TABLE payment.Payouts DROP CONSTRAINT [' + constraint_info.name + N'];'
    FROM sys.check_constraints AS constraint_info
    WHERE constraint_info.parent_object_id = OBJECT_ID(N'payment.Payouts')
      AND constraint_info.name IN (N'CK_Payouts_PeriodOrder', N'CK_Payouts_NetAmount', N'CK_Payouts_Status', N'CK_Payouts_LifecycleState');
    IF @sql <> N'' EXEC sp_executesql @sql;

    SET @sql = N'';
    SELECT @sql += N'ALTER TABLE payment.Payouts DROP CONSTRAINT [' + foreign_key.name + N'];'
    FROM sys.foreign_keys AS foreign_key
    JOIN sys.foreign_key_columns AS foreign_key_column
      ON foreign_key_column.constraint_object_id = foreign_key.object_id
    JOIN sys.columns AS column_info
      ON column_info.object_id = foreign_key_column.parent_object_id
     AND column_info.column_id = foreign_key_column.parent_column_id
    WHERE foreign_key.parent_object_id = OBJECT_ID(N'payment.Payouts')
      AND column_info.name = N'confirmed_by';
    IF @sql <> N'' EXEC sp_executesql @sql;

    IF COL_LENGTH(N'payment.Payouts', N'confirmed_at') IS NOT NULL
        ALTER TABLE payment.Payouts DROP COLUMN confirmed_at;
    IF COL_LENGTH(N'payment.Payouts', N'confirmed_by') IS NOT NULL
        ALTER TABLE payment.Payouts DROP COLUMN confirmed_by;

    IF EXISTS (
        SELECT 1 FROM sys.indexes
        WHERE object_id = OBJECT_ID(N'payment.Payouts')
          AND name = N'UX_Payouts_ActiveOperatorPeriod')
        DROP INDEX UX_Payouts_ActiveOperatorPeriod ON payment.Payouts;

    SET @sql = N'';
    SELECT @sql = N'ALTER TABLE payment.Payouts DROP CONSTRAINT [' + default_constraint.name + N'];'
    FROM sys.default_constraints AS default_constraint
    JOIN sys.columns AS column_info
      ON column_info.object_id = default_constraint.parent_object_id
     AND column_info.column_id = default_constraint.parent_column_id
    WHERE default_constraint.parent_object_id = OBJECT_ID(N'payment.Payouts')
      AND column_info.name = N'status';
    IF @sql <> N'' EXEC sp_executesql @sql;

    ALTER TABLE payment.Payouts ALTER COLUMN status VARCHAR(24) NOT NULL;
    IF NOT EXISTS (SELECT 1 FROM sys.default_constraints WHERE name = N'DF_Payouts_Status')
        ALTER TABLE payment.Payouts ADD CONSTRAINT DF_Payouts_Status DEFAULT 'PendingReview' FOR status;

    IF COL_LENGTH(N'payment.Payouts', N'reviewed_by') IS NULL
        ALTER TABLE payment.Payouts ADD reviewed_by BIGINT NULL;
    IF COL_LENGTH(N'payment.Payouts', N'reviewed_at') IS NULL
        ALTER TABLE payment.Payouts ADD reviewed_at DATETIME2 NULL;
    IF COL_LENGTH(N'payment.Payouts', N'rejection_reason') IS NULL
        ALTER TABLE payment.Payouts ADD rejection_reason NVARCHAR(500) NULL;
    IF COL_LENGTH(N'payment.Payouts', N'settlement_authorized_by') IS NULL
        ALTER TABLE payment.Payouts ADD settlement_authorized_by BIGINT NULL;
    IF COL_LENGTH(N'payment.Payouts', N'settlement_authorized_at') IS NULL
        ALTER TABLE payment.Payouts ADD settlement_authorized_at DATETIME2 NULL;
    IF COL_LENGTH(N'payment.Payouts', N'gateway_reference') IS NULL
        ALTER TABLE payment.Payouts ADD gateway_reference NVARCHAR(200) NULL;
    IF COL_LENGTH(N'payment.Payouts', N'gateway_failure_reason') IS NULL
        ALTER TABLE payment.Payouts ADD gateway_failure_reason NVARCHAR(500) NULL;
    IF COL_LENGTH(N'payment.Payouts', N'gateway_attempt_count') IS NULL
        ALTER TABLE payment.Payouts ADD gateway_attempt_count INT NOT NULL CONSTRAINT DF_Payouts_GatewayAttemptCount DEFAULT 0;
    IF COL_LENGTH(N'payment.Payouts', N'last_gateway_attempt_at') IS NULL
        ALTER TABLE payment.Payouts ADD last_gateway_attempt_at DATETIME2 NULL;

    IF NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = N'FK_Payouts_ReviewedBy')
        EXEC(N'ALTER TABLE payment.Payouts ADD CONSTRAINT FK_Payouts_ReviewedBy FOREIGN KEY (reviewed_by) REFERENCES dbo.Users(user_id);');
    IF NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = N'FK_Payouts_SettlementAuthorizedBy')
        EXEC(N'ALTER TABLE payment.Payouts ADD CONSTRAINT FK_Payouts_SettlementAuthorizedBy FOREIGN KEY (settlement_authorized_by) REFERENCES dbo.Users(user_id);');

    EXEC(N'ALTER TABLE payment.Payouts ADD CONSTRAINT CK_Payouts_PeriodOrder CHECK (period_end >= period_start);');
    EXEC(N'ALTER TABLE payment.Payouts ADD CONSTRAINT CK_Payouts_NetAmount CHECK (net_amount = gross_revenue - commission_amount);');
    EXEC(N'ALTER TABLE payment.Payouts ADD CONSTRAINT CK_Payouts_Status CHECK (status IN (''PendingReview'', ''ApprovedForSettlement'', ''SettlementProcessing'', ''Settled'', ''SettlementFailed'', ''Rejected''));');
    EXEC(N'ALTER TABLE payment.Payouts ADD CONSTRAINT CK_Payouts_LifecycleState CHECK (
        gateway_attempt_count >= 0
        AND ((status = ''PendingReview'' AND reviewed_by IS NULL AND reviewed_at IS NULL AND rejection_reason IS NULL
              AND settlement_authorized_by IS NULL AND settlement_authorized_at IS NULL AND gateway_reference IS NULL
              AND gateway_failure_reason IS NULL AND gateway_attempt_count = 0 AND last_gateway_attempt_at IS NULL)
          OR (status = ''ApprovedForSettlement'' AND reviewed_by IS NOT NULL AND reviewed_at IS NOT NULL AND rejection_reason IS NULL
              AND settlement_authorized_by IS NULL AND settlement_authorized_at IS NULL AND gateway_reference IS NULL
              AND gateway_failure_reason IS NULL AND gateway_attempt_count = 0 AND last_gateway_attempt_at IS NULL)
          OR (status = ''Rejected'' AND reviewed_by IS NOT NULL AND reviewed_at IS NOT NULL AND rejection_reason IS NOT NULL
              AND settlement_authorized_by IS NULL AND settlement_authorized_at IS NULL AND gateway_reference IS NULL
              AND gateway_failure_reason IS NULL AND gateway_attempt_count = 0 AND last_gateway_attempt_at IS NULL)
          OR (status = ''SettlementProcessing'' AND reviewed_by IS NOT NULL AND reviewed_at IS NOT NULL AND rejection_reason IS NULL
              AND settlement_authorized_by IS NOT NULL AND settlement_authorized_at IS NOT NULL AND gateway_failure_reason IS NULL)
          OR (status = ''Settled'' AND reviewed_by IS NOT NULL AND reviewed_at IS NOT NULL AND rejection_reason IS NULL
              AND settlement_authorized_by IS NOT NULL AND settlement_authorized_at IS NOT NULL AND gateway_reference IS NOT NULL
              AND gateway_failure_reason IS NULL AND gateway_attempt_count >= 1 AND last_gateway_attempt_at IS NOT NULL)
          OR (status = ''SettlementFailed'' AND reviewed_by IS NOT NULL AND reviewed_at IS NOT NULL AND rejection_reason IS NULL
              AND settlement_authorized_by IS NOT NULL AND settlement_authorized_at IS NOT NULL AND gateway_failure_reason IS NOT NULL
              AND gateway_attempt_count >= 1 AND last_gateway_attempt_at IS NOT NULL)));');

    IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'payment.Payouts') AND name = N'UX_Payouts_ActiveOperatorPeriod')
        EXEC(N'CREATE UNIQUE INDEX UX_Payouts_ActiveOperatorPeriod ON payment.Payouts(operator_user_id, period_start, period_end) WHERE status IN (''PendingReview'', ''ApprovedForSettlement'', ''SettlementProcessing'');');

    COMMIT TRANSACTION;
END TRY
BEGIN CATCH
    IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;
    THROW;
END CATCH;
GO
