/*
    UC-46 redesign: "Request Available Balance" instead of one request per settlement month.

    Rationale (agreed 2026-10-23): without aggregation, a low-revenue operator whose payable
    amount never individually crosses the minimum payout threshold in any single month could
    never request a payout at all. Real payout platforms (e.g. Travelpayouts in the travel
    affiliate space) solve this by carrying an unpaid balance forward and letting a single
    request claim everything outstanding. TripMate now does the same: one request pulls in every
    closed, eligible, not-yet-paid settlement month for the operator, and the payout snapshot
    keeps a per-month breakdown row for audit (so "which months funded this payout" is never lost).

    Changes:
    - payment.Payouts: BR-113 becomes "at most one active payout per operator" (not per month),
      since one open payout can now cover several months — a second concurrent request would
      double-claim the same balance. The old per-month unique index is replaced with a per-operator
      one. period_start/period_end now hold the earliest/latest contributing month, not a single
      settlement_month; settlement_month itself is no longer part of the uniqueness story.
    - payment.PayoutPeriodBreakdowns (new): one row per settlement month folded into a payout,
      with that month's own payments/refunds/commission/payable figures.
    - dbo.SystemConfigs: payout.minimum_amount_vnd updated to the confirmed value, 1,000,000 VND
      (supersedes the earlier unconfirmed 5,000,000 placeholder).

    Non-destructive and idempotent.
*/
SET ANSI_NULLS ON;
SET ANSI_PADDING ON;
SET ANSI_WARNINGS ON;
SET ARITHABORT ON;
SET CONCAT_NULL_YIELDS_NULL ON;
SET QUOTED_IDENTIFIER ON;
SET NUMERIC_ROUNDABORT OFF;
SET XACT_ABORT ON;
GO

BEGIN TRY
    BEGIN TRANSACTION;

    IF OBJECT_ID(N'payment.Payouts', N'U') IS NULL
        THROW 51000, 'UC-46 balance redesign requires payment.Payouts (20261022 migration).', 1;

    /* ---------- payment.Payouts: per-operator active-payout uniqueness ---------- */

    IF EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'payment.Payouts') AND name = N'UX_Payouts_ActiveOperatorMonth')
        DROP INDEX UX_Payouts_ActiveOperatorMonth ON payment.Payouts;

    IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'payment.Payouts') AND name = N'UX_Payouts_ActiveOperator')
        EXEC(N'CREATE UNIQUE INDEX UX_Payouts_ActiveOperator
            ON payment.Payouts(operator_user_id)
            WHERE status IN (''PendingReview'', ''ApprovedForSettlement'', ''SettlementProcessing'', ''SettlementFailed'');');

    IF COL_LENGTH(N'payment.Payouts', N'period_count') IS NULL
        ALTER TABLE payment.Payouts ADD period_count INT NOT NULL CONSTRAINT DF_Payouts_PeriodCount DEFAULT 1;

    /* ---------- payment.PayoutPeriodBreakdowns: new table ---------- */

    IF OBJECT_ID(N'payment.PayoutPeriodBreakdowns', N'U') IS NULL
    BEGIN
        CREATE TABLE payment.PayoutPeriodBreakdowns (
            breakdown_id            BIGINT IDENTITY(1,1) PRIMARY KEY,
            payout_id               BIGINT NOT NULL REFERENCES payment.Payouts(payout_id) ON DELETE CASCADE,
            settlement_month        DATE NOT NULL,
            eligible_booking_count  INT NOT NULL CHECK (eligible_booking_count >= 0),
            payments_received       DECIMAL(14,2) NOT NULL CHECK (payments_received >= 0),
            discounts_given         DECIMAL(14,2) NOT NULL CHECK (discounts_given >= 0),
            refunded_amount         DECIMAL(14,2) NOT NULL CHECK (refunded_amount >= 0),
            commission_amount       DECIMAL(14,2) NOT NULL CHECK (commission_amount >= 0),
            payable_net_amount      DECIMAL(14,2) NOT NULL,
            CONSTRAINT UQ_PayoutPeriodBreakdowns UNIQUE (payout_id, settlement_month)
        );
        CREATE INDEX IX_PayoutPeriodBreakdowns_Payout ON payment.PayoutPeriodBreakdowns(payout_id);
    END;

    /* ---------- dbo.SystemConfigs: confirmed minimum payout (2026-10-23) ---------- */

    IF EXISTS (SELECT 1 FROM dbo.SystemConfigs WHERE config_key = N'payout.minimum_amount_vnd')
        UPDATE dbo.SystemConfigs
        SET config_value = N'1000000',
            description = N'UC-46: minimum payable net amount (VND) for a payout request to be allowed. Confirmed 2026-10-23 (supersedes the earlier unconfirmed 5,000,000 placeholder). Production value; lower dev/staging values are a manual config update in that environment only, never an environment check in code.',
            updated_at = SYSUTCDATETIME()
        WHERE config_key = N'payout.minimum_amount_vnd';
    ELSE
        INSERT INTO dbo.SystemConfigs (config_key, config_value, description, updated_at)
        VALUES (N'payout.minimum_amount_vnd', N'1000000',
            N'UC-46: minimum payable net amount (VND) for a payout request to be allowed. Confirmed 2026-10-23.',
            SYSUTCDATETIME());

    COMMIT TRANSACTION;
END TRY
BEGIN CATCH
    IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;
    THROW;
END CATCH;
GO
