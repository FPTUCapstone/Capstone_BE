/*
    DAILY-PAYOUT-TRANSFER (plan Task 2B, spec v6, Q3): archive and remove the legacy Main/Retained
    payment.PayoutSettlement rows created on this environment before v6, together with the
    gateway attempts of the batches that only ever held such rows.

    MUST run before 20261112_daily_payout_v6_schema.sql, which refuses to apply while any
    Main/Retained row still exists. payment.PayoutBatch itself is NOT touched here (out of
    scope for this migration): a batch row survives even after its settlements are archived.

    Procedure, inside one transaction:
    1. Create payment.PayoutSettlement_Archive_v3 / payment.PayoutSettlementAttempt_Archive_v3
       (same shape as the source tables, plus archived_at_utc) if they do not already exist.
    2. Copy every Main/Retained settlement row, and every attempt belonging to a batch that is
       made up entirely of Main/Retained rows, skipping rows already archived (idempotent).
    3. Verify the archived count matches the live count before deleting anything.
    4. Delete the archived attempts, then the archived settlement rows, from the live tables.

    On a database with no legacy rows this script is a no-op (idempotent re-run safe).
*/
SET ANSI_NULLS ON;
SET ANSI_PADDING ON;
SET ANSI_WARNINGS ON;
SET ARITHABORT ON;
SET CONCAT_NULL_YIELDS_NULL ON;
SET QUOTED_IDENTIFIER ON;
SET NUMERIC_ROUNDABORT OFF;
GO

IF OBJECT_ID(N'payment.PayoutSettlement_Archive_v3', N'U') IS NULL
BEGIN
    CREATE TABLE payment.PayoutSettlement_Archive_v3 (
        id                          BIGINT NOT NULL CONSTRAINT PK_PayoutSettlement_Archive_v3 PRIMARY KEY,
        batch_id                    BIGINT NULL,
        booking_id                  BIGINT NOT NULL,
        tour_operator_user_id       BIGINT NOT NULL,
        run_date                    DATE NOT NULL,
        kind                        NVARCHAR(16) NOT NULL,
        gross_amount                DECIMAL(18,2) NOT NULL,
        commission_rate             DECIMAL(5,2) NOT NULL,
        commission_amount           DECIMAL(18,2) NOT NULL,
        net_amount                  DECIMAL(18,2) NOT NULL,
        debt_offset_amount          DECIMAL(18,2) NOT NULL,
        payout_amount               DECIMAL(18,2) NOT NULL,
        status                      NVARCHAR(24) NOT NULL,
        transfer_reference          NVARCHAR(100) NULL,
        attempt_count               INT NOT NULL,
        last_attempt_at_utc         DATETIME2 NULL,
        created_at_utc              DATETIME2 NOT NULL,
        updated_at_utc              DATETIME2 NOT NULL,
        archived_at_utc             DATETIME2 NOT NULL CONSTRAINT DF_PayoutSettlement_Archive_v3_archived DEFAULT SYSUTCDATETIME()
    );
END
GO

IF OBJECT_ID(N'payment.PayoutSettlementAttempt_Archive_v3', N'U') IS NULL
BEGIN
    CREATE TABLE payment.PayoutSettlementAttempt_Archive_v3 (
        id                          BIGINT NOT NULL CONSTRAINT PK_PayoutSettlementAttempt_Archive_v3 PRIMARY KEY,
        batch_id                    BIGINT NOT NULL,
        attempt_number              INT NOT NULL,
        sent_at_utc                 DATETIME2 NOT NULL,
        gateway_reference           NVARCHAR(100) NOT NULL,
        outcome                     NVARCHAR(24) NOT NULL,
        failure_reason              NVARCHAR(500) NULL,
        archived_at_utc             DATETIME2 NOT NULL CONSTRAINT DF_PayoutSettlementAttempt_Archive_v3_archived DEFAULT SYSUTCDATETIME()
    );
END
GO

BEGIN TRANSACTION;

-- Batches made up entirely of legacy Main/Retained rows: their attempts are archived too.
IF OBJECT_ID(N'tempdb..#LegacyOnlyBatches') IS NOT NULL
    DROP TABLE #LegacyOnlyBatches;
SELECT DISTINCT s.batch_id
INTO #LegacyOnlyBatches
FROM payment.PayoutSettlement AS s
WHERE s.kind IN (N'Main', N'Retained')
  AND s.batch_id IS NOT NULL
  AND NOT EXISTS (
        SELECT 1 FROM payment.PayoutSettlement AS other
        WHERE other.batch_id = s.batch_id
          AND other.kind NOT IN (N'Main', N'Retained'));

INSERT INTO payment.PayoutSettlementAttempt_Archive_v3 (
    id, batch_id, attempt_number, sent_at_utc, gateway_reference, outcome, failure_reason)
SELECT a.id, a.batch_id, a.attempt_number, a.sent_at_utc, a.gateway_reference, a.outcome, a.failure_reason
FROM payment.PayoutSettlementAttempt AS a
WHERE a.batch_id IN (SELECT batch_id FROM #LegacyOnlyBatches)
  AND NOT EXISTS (
        SELECT 1 FROM payment.PayoutSettlementAttempt_Archive_v3 AS archived
        WHERE archived.id = a.id);

INSERT INTO payment.PayoutSettlement_Archive_v3 (
    id, batch_id, booking_id, tour_operator_user_id, run_date, kind, gross_amount, commission_rate,
    commission_amount, net_amount, debt_offset_amount, payout_amount, status, transfer_reference,
    attempt_count, last_attempt_at_utc, created_at_utc, updated_at_utc)
SELECT s.id, s.batch_id, s.booking_id, s.tour_operator_user_id, s.run_date, s.kind, s.gross_amount,
       s.commission_rate, s.commission_amount, s.net_amount, s.debt_offset_amount, s.payout_amount,
       s.status, s.transfer_reference, s.attempt_count, s.last_attempt_at_utc, s.created_at_utc, s.updated_at_utc
FROM payment.PayoutSettlement AS s
WHERE s.kind IN (N'Main', N'Retained')
  AND NOT EXISTS (
        SELECT 1 FROM payment.PayoutSettlement_Archive_v3 AS archived
        WHERE archived.id = s.id);

-- Verify the archive holds every legacy row before deleting anything from the live tables.
IF (SELECT COUNT(*) FROM payment.PayoutSettlement WHERE kind IN (N'Main', N'Retained'))
   <> (SELECT COUNT(*) FROM payment.PayoutSettlement_Archive_v3
       WHERE id IN (SELECT id FROM payment.PayoutSettlement WHERE kind IN (N'Main', N'Retained')))
BEGIN
    ROLLBACK TRANSACTION;
    THROW 51010, N'20261112_daily_payout_v6_archive_main_rows.sql: archived row count does not match live Main/Retained row count. Aborted without changing data.', 1;
END

DELETE a
FROM payment.PayoutSettlementAttempt AS a
WHERE a.batch_id IN (SELECT batch_id FROM #LegacyOnlyBatches);

DELETE FROM payment.PayoutSettlement WHERE kind IN (N'Main', N'Retained');

DROP TABLE #LegacyOnlyBatches;

COMMIT TRANSACTION;
GO
