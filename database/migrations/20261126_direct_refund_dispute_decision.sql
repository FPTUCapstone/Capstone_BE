-- Adds dispute resolution columns to DirectRefundCases (admin decision audit)
-- Phase 3 — direct payout refund flow
SET XACT_ABORT ON;
SET NOCOUNT ON;

BEGIN TRY
    BEGIN TRANSACTION;

    IF OBJECT_ID(N'payment.DirectRefundCases', N'U') IS NULL
        THROW 51005, N'payment.DirectRefundCases does not exist.', 1;

    -- dispute_resolution: null until admin acts; then 'Completed','CorrectionRequired','MoreEvidenceRequired'
    IF NOT EXISTS (
        SELECT 1 FROM sys.columns
        WHERE object_id = OBJECT_ID(N'payment.DirectRefundCases')
          AND name = N'dispute_resolution')
    BEGIN
        ALTER TABLE payment.DirectRefundCases
            ADD dispute_resolution VARCHAR(30) NULL;
    END;

    IF NOT EXISTS (
        SELECT 1 FROM sys.columns
        WHERE object_id = OBJECT_ID(N'payment.DirectRefundCases')
          AND name = N'dispute_resolution_reason')
    BEGIN
        ALTER TABLE payment.DirectRefundCases
            ADD dispute_resolution_reason NVARCHAR(1000) NULL;
    END;

    IF NOT EXISTS (
        SELECT 1 FROM sys.columns
        WHERE object_id = OBJECT_ID(N'payment.DirectRefundCases')
          AND name = N'dispute_resolved_at')
    BEGIN
        ALTER TABLE payment.DirectRefundCases
            ADD dispute_resolved_at DATETIME2 NULL;
    END;

    COMMIT TRANSACTION;
END TRY
BEGIN CATCH
    IF @@TRANCOUNT > 0
        ROLLBACK TRANSACTION;
    THROW;
END CATCH;
