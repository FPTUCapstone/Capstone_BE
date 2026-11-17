/*
    DAILY-PAYOUT-TRANSFER (plan Task 7A, spec PD-17): payout reconciliation items.

    Adds payment.PayoutReconciliationItem — one row per discrepancy the retry sweep's PD-7a
    gateway lookup finds between what TripMate still believed (a batch stuck Processing) and
    what the gateway actually reports (Succeeded or Rejected). A plain Failed/Timeout lookup
    (still unresolved, consistent with Processing) is not a discrepancy and writes nothing here.

    Non-destructive and idempotent: the CREATE is guarded by an OBJECT_ID check.
*/
SET ANSI_NULLS ON;
SET ANSI_PADDING ON;
SET ANSI_WARNINGS ON;
SET ARITHABORT ON;
SET CONCAT_NULL_YIELDS_NULL ON;
SET QUOTED_IDENTIFIER ON;
SET NUMERIC_ROUNDABORT OFF;
GO

IF OBJECT_ID(N'payment.PayoutReconciliationItem', N'U') IS NULL
BEGIN
    CREATE TABLE payment.PayoutReconciliationItem (
        id                          BIGINT IDENTITY(1,1) NOT NULL CONSTRAINT PK_PayoutReconciliationItem PRIMARY KEY,
        batch_id                    BIGINT NOT NULL CONSTRAINT FK_PayoutReconciliationItem_Batch REFERENCES payment.PayoutBatch (id),
        detected_at_utc             DATETIME2 NOT NULL,
        expected_status             NVARCHAR(24) NOT NULL,
        observed_outcome            NVARCHAR(24) NOT NULL,
        observed_reference          NVARCHAR(100) NULL,
        note                        NVARCHAR(500) NULL,
        CONSTRAINT CK_PayoutReconciliationItem_outcome CHECK (observed_outcome IN (N'Succeeded', N'Rejected'))
    );
    CREATE INDEX IX_PayoutReconciliationItem_Batch ON payment.PayoutReconciliationItem (batch_id);
END
GO
