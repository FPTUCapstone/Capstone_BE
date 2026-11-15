/*
    DAILY-PAYOUT-TRANSFER (plan Task 2A, spec v6): schema for Advance/Final settlements.

    - payment.PayoutSettlement.kind: Main/Retained are replaced by Advance (70% after the
      24-hour Awaiting Window, BR-141) and Final (20% at the first 10:00 run after tour end, Q6).
    - Status 'Resolved' is added (UC-73 [Mark as Resolved]); 'Carried Over' is removed (carry over
      is not in SRS rev10-5). Same status vocabulary for payment.PayoutBatch.
    - dbo.Users: lock_reason / locked_at_utc record the automatic lock for unrecovered pending debt
      (BR-189, Q4, Q5). No percentage column is added (Q5).
    - payment.TourOperatorDebt: the outstanding-debt ledger that payout reads (PD-15) and the
      refund branch writes to. Timestamps are DATETIME2.

    Preconditions: 20261108_daily_payout_settlement_foundation.sql has run, and
    20261112_daily_payout_v6_archive_main_rows.sql (Task 2B) has moved every legacy Main/Retained
    row out of payment.PayoutSettlement. This script refuses to run otherwise.

    Idempotent: every change is guarded, so a re-run on an upgraded database is a no-op.
*/
SET ANSI_NULLS ON;
SET ANSI_PADDING ON;
SET ANSI_WARNINGS ON;
SET ARITHABORT ON;
SET CONCAT_NULL_YIELDS_NULL ON;
SET QUOTED_IDENTIFIER ON;
SET NUMERIC_ROUNDABORT OFF;
GO

IF EXISTS (
    SELECT 1 FROM payment.PayoutSettlement
    WHERE kind NOT IN (N'Advance', N'Final'))
BEGIN
    THROW 51000, N'20261112_daily_payout_v6_schema.sql: payment.PayoutSettlement still has legacy Main/Retained rows. Run 20261112_daily_payout_v6_archive_main_rows.sql first.', 1;
END
GO

IF EXISTS (
    SELECT 1 FROM payment.PayoutSettlement WHERE status = N'Carried Over'
    UNION ALL
    SELECT 1 FROM payment.PayoutBatch WHERE status = N'Carried Over')
BEGIN
    THROW 51001, N'20261112_daily_payout_v6_schema.sql: Carried Over rows exist. Carry over is removed in v6; resolve these rows before applying this script.', 1;
END
GO

-- Kind: Advance / Final only.
IF EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = N'CK_PayoutSettlement_kind')
    ALTER TABLE payment.PayoutSettlement DROP CONSTRAINT CK_PayoutSettlement_kind;
GO
ALTER TABLE payment.PayoutSettlement ADD CONSTRAINT CK_PayoutSettlement_kind
    CHECK (kind IN (N'Advance', N'Final'));
GO

-- Settlement status: Resolved added, Carried Over removed.
IF EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = N'CK_PayoutSettlement_status')
    ALTER TABLE payment.PayoutSettlement DROP CONSTRAINT CK_PayoutSettlement_status;
GO
ALTER TABLE payment.PayoutSettlement ADD CONSTRAINT CK_PayoutSettlement_status
    CHECK (status IN (N'Scheduled', N'Processing', N'Settled', N'SettlementFailed', N'Resolved'));
GO

-- Batch status: same vocabulary as settlements.
IF EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = N'CK_PayoutBatch_status')
    ALTER TABLE payment.PayoutBatch DROP CONSTRAINT CK_PayoutBatch_status;
GO
ALTER TABLE payment.PayoutBatch ADD CONSTRAINT CK_PayoutBatch_status
    CHECK (status IN (N'Scheduled', N'Processing', N'Settled', N'SettlementFailed', N'Resolved'));
GO

-- Automatic lock for unrecovered pending debt (BR-189): reason and timestamp on the user row.
IF COL_LENGTH(N'dbo.Users', N'lock_reason') IS NULL
    ALTER TABLE dbo.Users ADD lock_reason NVARCHAR(200) NULL;
GO
IF COL_LENGTH(N'dbo.Users', N'locked_at_utc') IS NULL
    ALTER TABLE dbo.Users ADD locked_at_utc DATETIME2 NULL;
GO

-- Outstanding debt of a Tour Operator (PD-15). recovered_amount never exceeds amount.
IF OBJECT_ID(N'payment.TourOperatorDebt', N'U') IS NULL
BEGIN
    CREATE TABLE payment.TourOperatorDebt (
        id                          BIGINT IDENTITY(1,1) NOT NULL CONSTRAINT PK_TourOperatorDebt PRIMARY KEY,
        tour_operator_user_id       BIGINT NOT NULL CONSTRAINT FK_TourOperatorDebt_Operator REFERENCES dbo.Users (user_id),
        amount                      DECIMAL(18,2) NOT NULL,
        recovered_amount            DECIMAL(18,2) NOT NULL CONSTRAINT DF_TourOperatorDebt_recovered DEFAULT 0,
        incurred_at_utc             DATETIME2 NOT NULL,
        created_at_utc              DATETIME2 NOT NULL CONSTRAINT DF_TourOperatorDebt_created DEFAULT SYSUTCDATETIME(),
        CONSTRAINT CK_TourOperatorDebt_amount CHECK (amount > 0),
        CONSTRAINT CK_TourOperatorDebt_recovered_nonnegative CHECK (recovered_amount >= 0),
        CONSTRAINT CK_TourOperatorDebt_recovered_within_amount CHECK (recovered_amount <= amount)
    );
    CREATE INDEX IX_TourOperatorDebt_Operator_Incurred ON payment.TourOperatorDebt (tour_operator_user_id, incurred_at_utc);
END
GO
