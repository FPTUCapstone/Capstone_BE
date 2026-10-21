/* UC-42 — durable automatic-refund processing state.
   A cancellation has already fixed the amount. This migration only adds evidence needed to
   submit/retry that refund safely; it never recalculates a historic cancellation policy. */
SET XACT_ABORT ON;
BEGIN TRANSACTION;

IF COL_LENGTH(N'payment.Refunds', N'gateway_reference') IS NULL
    ALTER TABLE payment.Refunds ADD gateway_reference NVARCHAR(150) NULL;
IF COL_LENGTH(N'payment.Refunds', N'attempt_count') IS NULL
    ALTER TABLE payment.Refunds ADD attempt_count INT NOT NULL
        CONSTRAINT DF_Refunds_AttemptCount DEFAULT (0);
IF COL_LENGTH(N'payment.Refunds', N'last_attempt_at') IS NULL
    ALTER TABLE payment.Refunds ADD last_attempt_at DATETIME2 NULL;
IF COL_LENGTH(N'payment.Refunds', N'next_attempt_at') IS NULL
    ALTER TABLE payment.Refunds ADD next_attempt_at DATETIME2 NULL;
IF COL_LENGTH(N'payment.Refunds', N'last_error_code') IS NULL
    ALTER TABLE payment.Refunds ADD last_error_code NVARCHAR(100) NULL;
IF COL_LENGTH(N'payment.Refunds', N'last_error_message') IS NULL
    ALTER TABLE payment.Refunds ADD last_error_message NVARCHAR(1000) NULL;
IF COL_LENGTH(N'payment.PaymentTransactions', N'refund_id') IS NULL
    ALTER TABLE payment.PaymentTransactions ADD refund_id BIGINT NULL;

GO

-- The pre-UC-42 schema stores status as VARCHAR(10), which cannot hold RetryPending.
-- Preserve the existing Pending default while widening the legacy column before adding the
-- expanded state-machine constraint below.
IF COL_LENGTH(N'payment.Refunds', N'status') <> 20
    ALTER TABLE payment.Refunds ALTER COLUMN status VARCHAR(20) NOT NULL;

UPDATE payment.Refunds
SET next_attempt_at = requested_at
WHERE status = 'Pending' AND next_attempt_at IS NULL;

DECLARE @statusConstraint sysname;
DECLARE @dropStatusConstraintSql nvarchar(max);
SELECT @statusConstraint = cc.name
FROM sys.check_constraints AS cc
WHERE cc.parent_object_id = OBJECT_ID(N'payment.Refunds')
  AND cc.definition LIKE N'%status%'
  AND cc.definition LIKE N'%Pending%';
IF @statusConstraint IS NOT NULL
BEGIN
    SET @dropStatusConstraintSql = N'ALTER TABLE payment.Refunds DROP CONSTRAINT ' + QUOTENAME(@statusConstraint) + N';';
    EXECUTE (@dropStatusConstraintSql);
END;
ALTER TABLE payment.Refunds ADD CONSTRAINT CK_Refunds_Status
    CHECK (status IN ('Pending','Processing','RetryPending','Processed','Failed','Rejected'));

IF NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE parent_object_id = OBJECT_ID(N'payment.Refunds')
               AND name = N'CK_Refunds_AttemptCount')
    ALTER TABLE payment.Refunds ADD CONSTRAINT CK_Refunds_AttemptCount CHECK (attempt_count >= 0);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'payment.Refunds')
               AND name = N'IX_Refunds_Due')
    CREATE INDEX IX_Refunds_Due ON payment.Refunds(status, next_attempt_at);
IF NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE parent_object_id = OBJECT_ID(N'payment.PaymentTransactions')
               AND name = N'FK_PaymentTransactions_Refund')
    ALTER TABLE payment.PaymentTransactions ADD CONSTRAINT FK_PaymentTransactions_Refund
        FOREIGN KEY (refund_id) REFERENCES payment.Refunds(refund_id);

COMMIT TRANSACTION;
