/*
    DAILY-PAYOUT-TRANSFER (Task 2): daily payout settlement foundation.

    Adds the tables for the automatic daily transfer to Tour Operators (SRS v11, UC-71,
    UC-72, UC-74; spec DAILY-PAYOUT-TRANSFER-be-spec.md v2):
    - payment.PayoutRun: one row per daily run (RunDate is unique, so re-running a day is
      a no-op — PD-11).
    - payment.PayoutBatch: one transfer order per Tour Operator per run (PD-14).
    - payment.PayoutSettlement: one row per booking (Kind = 'Main') or per retained part
      of a partially refunded booking (Kind = 'Retained') (PD-2, PD-5).
    - payment.PayoutSettlementAttempt: every gateway call and its outcome (PC-03, PD-7a).

    Commission: the rate used for a settlement is copied from the booking's commission
    snapshot at confirmation (payment.PaymentTransactions.CommissionRateApplied, BR-140).
    No commission column is added to commerce.Bookings. Booking rows created before this
    migration are settled with the system rate in force (10%), see plan Task 2.

    Non-destructive and idempotent: every CREATE is guarded by OBJECT_ID / sys.indexes checks.
    Existing payment.Payouts / payment.PayoutItems are NOT touched here (removal is Task 3).
*/
SET ANSI_NULLS ON;
SET ANSI_PADDING ON;
SET ANSI_WARNINGS ON;
SET ARITHABORT ON;
SET CONCAT_NULL_YIELDS_NULL ON;
SET QUOTED_IDENTIFIER ON;
SET NUMERIC_ROUNDABORT OFF;
GO

IF SCHEMA_ID(N'payment') IS NULL
    EXEC(N'CREATE SCHEMA payment');
GO

IF OBJECT_ID(N'payment.PayoutRun', N'U') IS NULL
BEGIN
    CREATE TABLE payment.PayoutRun (
        id                  BIGINT IDENTITY(1,1) NOT NULL CONSTRAINT PK_PayoutRun PRIMARY KEY,
        run_date            DATE NOT NULL,
        status              NVARCHAR(24) NOT NULL,
        started_at_utc      DATETIME2 NOT NULL,
        completed_at_utc    DATETIME2 NULL,
        created_at_utc      DATETIME2 NOT NULL CONSTRAINT DF_PayoutRun_created DEFAULT SYSUTCDATETIME(),
        CONSTRAINT CK_PayoutRun_status CHECK (status IN (N'Running', N'Completed', N'Failed'))
    );
    CREATE UNIQUE INDEX UX_PayoutRun_RunDate ON payment.PayoutRun (run_date);
END
GO

IF OBJECT_ID(N'payment.PayoutBatch', N'U') IS NULL
BEGIN
    CREATE TABLE payment.PayoutBatch (
        id                          BIGINT IDENTITY(1,1) NOT NULL CONSTRAINT PK_PayoutBatch PRIMARY KEY,
        run_date                    DATE NOT NULL,
        tour_operator_user_id       BIGINT NOT NULL CONSTRAINT FK_PayoutBatch_Operator REFERENCES dbo.Users (user_id),
        total_amount                DECIMAL(18,2) NOT NULL,
        status                      NVARCHAR(24) NOT NULL,
        transfer_reference          NVARCHAR(100) NULL,
        attempt_count               INT NOT NULL CONSTRAINT DF_PayoutBatch_attempts DEFAULT 0,
        last_attempt_at_utc         DATETIME2 NULL,
        created_at_utc              DATETIME2 NOT NULL CONSTRAINT DF_PayoutBatch_created DEFAULT SYSUTCDATETIME(),
        updated_at_utc              DATETIME2 NOT NULL CONSTRAINT DF_PayoutBatch_updated DEFAULT SYSUTCDATETIME(),
        CONSTRAINT CK_PayoutBatch_total CHECK (total_amount >= 0),
        CONSTRAINT CK_PayoutBatch_status CHECK (status IN (N'Scheduled', N'Processing', N'Settled', N'SettlementFailed', N'Carried Over'))
    );
    CREATE UNIQUE INDEX UX_PayoutBatch_RunDate_Operator ON payment.PayoutBatch (run_date, tour_operator_user_id);
    CREATE INDEX IX_PayoutBatch_Status ON payment.PayoutBatch (status);
END
GO

IF OBJECT_ID(N'payment.PayoutSettlement', N'U') IS NULL
BEGIN
    CREATE TABLE payment.PayoutSettlement (
        id                          BIGINT IDENTITY(1,1) NOT NULL CONSTRAINT PK_PayoutSettlement PRIMARY KEY,
        batch_id                    BIGINT NULL CONSTRAINT FK_PayoutSettlement_Batch REFERENCES payment.PayoutBatch (id),
        booking_id                  BIGINT NOT NULL CONSTRAINT FK_PayoutSettlement_Booking REFERENCES commerce.Bookings (booking_id),
        tour_operator_user_id       BIGINT NOT NULL CONSTRAINT FK_PayoutSettlement_Operator REFERENCES dbo.Users (user_id),
        run_date                    DATE NOT NULL,
        kind                        NVARCHAR(16) NOT NULL,
        gross_amount                DECIMAL(18,2) NOT NULL,
        commission_rate             DECIMAL(5,2) NOT NULL,
        commission_amount           DECIMAL(18,2) NOT NULL,
        net_amount                  DECIMAL(18,2) NOT NULL,
        debt_offset_amount          DECIMAL(18,2) NOT NULL CONSTRAINT DF_PayoutSettlement_debt DEFAULT 0,
        payout_amount               DECIMAL(18,2) NOT NULL,
        status                      NVARCHAR(24) NOT NULL,
        transfer_reference          NVARCHAR(100) NULL,
        attempt_count               INT NOT NULL CONSTRAINT DF_PayoutSettlement_attempts DEFAULT 0,
        last_attempt_at_utc         DATETIME2 NULL,
        created_at_utc              DATETIME2 NOT NULL CONSTRAINT DF_PayoutSettlement_created DEFAULT SYSUTCDATETIME(),
        updated_at_utc              DATETIME2 NOT NULL CONSTRAINT DF_PayoutSettlement_updated DEFAULT SYSUTCDATETIME(),
        CONSTRAINT CK_PayoutSettlement_kind CHECK (kind IN (N'Main', N'Retained')),
        CONSTRAINT CK_PayoutSettlement_status CHECK (status IN (N'Scheduled', N'Processing', N'Settled', N'SettlementFailed', N'Carried Over')),
        CONSTRAINT CK_PayoutSettlement_rate CHECK (commission_rate >= 0 AND commission_rate <= 100),
        CONSTRAINT CK_PayoutSettlement_gross CHECK (gross_amount >= 0),
        CONSTRAINT CK_PayoutSettlement_commission CHECK (commission_amount >= 0 AND commission_amount <= gross_amount),
        CONSTRAINT CK_PayoutSettlement_net CHECK (net_amount = gross_amount - commission_amount),
        -- payout_amount = 0 is allowed: a booking whose net amount was fully absorbed by the
        -- Tour Operator's pending debt (PD-3, PD-15) still gets a settlement row (so it is
        -- never re-selected by a later run, PD-11) even though nothing is transferred for it.
        CONSTRAINT CK_PayoutSettlement_payout_nonnegative CHECK (payout_amount >= 0)
    );
    -- PD-5: at most one Main settlement per booking; a Retained settlement is a separate kind.
    CREATE UNIQUE INDEX UX_PayoutSettlement_Booking_Kind ON payment.PayoutSettlement (booking_id, kind);
    CREATE INDEX IX_PayoutSettlement_Operator_Run ON payment.PayoutSettlement (tour_operator_user_id, run_date);
    CREATE INDEX IX_PayoutSettlement_Batch ON payment.PayoutSettlement (batch_id);
    CREATE INDEX IX_PayoutSettlement_Status ON payment.PayoutSettlement (status);
END
GO

IF OBJECT_ID(N'payment.PayoutSettlementAttempt', N'U') IS NULL
BEGIN
    CREATE TABLE payment.PayoutSettlementAttempt (
        id                          BIGINT IDENTITY(1,1) NOT NULL CONSTRAINT PK_PayoutSettlementAttempt PRIMARY KEY,
        batch_id                    BIGINT NOT NULL CONSTRAINT FK_PayoutSettlementAttempt_Batch REFERENCES payment.PayoutBatch (id),
        attempt_number              INT NOT NULL,
        sent_at_utc                 DATETIME2 NOT NULL,
        gateway_reference           NVARCHAR(100) NOT NULL,
        outcome                     NVARCHAR(24) NOT NULL,
        failure_reason              NVARCHAR(500) NULL,
        CONSTRAINT CK_PayoutSettlementAttempt_outcome CHECK (outcome IN (N'Sent', N'Succeeded', N'Failed', N'Timeout', N'Rejected')),
        CONSTRAINT UX_PayoutSettlementAttempt_Batch_Number UNIQUE (batch_id, attempt_number)
    );
END
GO

-- Task 2B: carried-over amounts (PD-10, PD-5). Named distinctly from the pre-existing
-- payment.PayoutAdjustments table (post-trip-refund branch) to avoid a collision.
IF OBJECT_ID(N'payment.PayoutSettlementAdjustment', N'U') IS NULL
BEGIN
    CREATE TABLE payment.PayoutSettlementAdjustment (
        id                          BIGINT IDENTITY(1,1) NOT NULL CONSTRAINT PK_PayoutSettlementAdjustment PRIMARY KEY,
        source_settlement_id        BIGINT NOT NULL CONSTRAINT FK_PayoutSettlementAdjustment_Source REFERENCES payment.PayoutSettlement (id),
        tour_operator_user_id       BIGINT NOT NULL CONSTRAINT FK_PayoutSettlementAdjustment_Operator REFERENCES dbo.Users (user_id),
        amount                      DECIMAL(18,2) NOT NULL,
        reason                      NVARCHAR(200) NOT NULL,
        created_at_utc              DATETIME2 NOT NULL CONSTRAINT DF_PayoutSettlementAdjustment_created DEFAULT SYSUTCDATETIME(),
        applied_batch_id            BIGINT NULL CONSTRAINT FK_PayoutSettlementAdjustment_Batch REFERENCES payment.PayoutBatch (id),
        applied_at_utc              DATETIME2 NULL,
        CONSTRAINT CK_PayoutSettlementAdjustment_amount CHECK (amount > 0),
        CONSTRAINT UX_PayoutSettlementAdjustment_Source UNIQUE (source_settlement_id)
    );
    CREATE INDEX IX_PayoutSettlementAdjustment_Operator_Outstanding ON payment.PayoutSettlementAdjustment (tour_operator_user_id)
        WHERE applied_batch_id IS NULL;
END
GO

-- Patch for a database where this migration already ran with the earlier, stricter
-- CK_PayoutSettlement_payout_positive constraint (payout_amount > 0): replace it with the
-- >= 0 constraint above. A no-op on a database that never had the old constraint.
IF EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = N'CK_PayoutSettlement_payout_positive')
BEGIN
    ALTER TABLE payment.PayoutSettlement DROP CONSTRAINT CK_PayoutSettlement_payout_positive;
END
GO
IF NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = N'CK_PayoutSettlement_payout_nonnegative')
BEGIN
    ALTER TABLE payment.PayoutSettlement ADD CONSTRAINT CK_PayoutSettlement_payout_nonnegative CHECK (payout_amount >= 0);
END
GO
