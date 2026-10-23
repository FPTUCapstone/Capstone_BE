/*
    UC-46: Request Payout Settlement.

    Adds the columns and tables needed for an operator-initiated payout request:
    - payment.Payouts gains payout_code, a settlement month marker, the full amount
      breakdown (payments received / discounts / refunds / net sales / commission /
      adjustments / payable net), request counters, and a snapshot of the beneficiary
      bank account used for this specific payout (bank details can change later without
      altering an already-submitted request).
    - payment.Payouts' status CHECK gains 'Cancelled' (operator-initiated cancellation of
      a still-PendingReview request), and the active-request unique index now also treats
      'SettlementFailed' as active, so a failed gateway transfer does not let a second
      request be created for the same operator/month while Staff/Administrator retry it.
    - payment.PayoutItems gains per-payment snapshot columns (payment_transaction_id,
      paid_amount, discount_amount, commission_rate_applied, commission_amount) so the
      payout is a self-contained accounting record, not just a list of booking ids.
    - payment.PayoutRefundItems is new: one row per refund that reduced this payout's net
      amount, with the commission rate it reversed.
    - payment.PayoutAdjustments is new: a ledger of refunds that completed too late to be
      included in the payout they belong to (after that payout's snapshot was taken).
      Each adjustment is applied to a later payout request for the same operator, never
      retroactively changing an already-submitted snapshot.
    - dbo.OperatorProfiles gains beneficiary bank columns. The full account number is
      stored only in protected (encrypted) form; a masked value is kept for display so
      the API and UI never need to decrypt it to show it back to the operator.
    - dbo.SystemConfigs is seeded with payout.minimum_amount_vnd (production value;
      lower dev/staging values are set by a manual config update in that environment,
      never by an environment check in application code) and payout.refund_hold_days
      (the number of days after a tour's scheduled end before its bookings are
      considered final enough to pay out — see UC-46 BE spec OPEN-1).

    Non-destructive and idempotent: every ALTER/CREATE is guarded, so re-running this
    script on a database that already has these columns/tables is a safe no-op.
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
        THROW 51000, 'UC-46 requires payment.Payouts.', 1;
    IF OBJECT_ID(N'payment.PayoutItems', N'U') IS NULL
        THROW 51000, 'UC-46 requires payment.PayoutItems.', 1;
    IF OBJECT_ID(N'dbo.OperatorProfiles', N'U') IS NULL
        THROW 51000, 'UC-46 requires dbo.OperatorProfiles.', 1;
    IF OBJECT_ID(N'payment.Refunds', N'U') IS NULL
        THROW 51000, 'UC-46 requires payment.Refunds.', 1;

    /* ---------- payment.Payouts: new columns ---------- */

    IF COL_LENGTH(N'payment.Payouts', N'payout_code') IS NULL
        ALTER TABLE payment.Payouts ADD payout_code VARCHAR(24) NULL;

    IF COL_LENGTH(N'payment.Payouts', N'settlement_month') IS NULL
        ALTER TABLE payment.Payouts ADD settlement_month DATE NULL;

    IF COL_LENGTH(N'payment.Payouts', N'cut_off_at_utc') IS NULL
        ALTER TABLE payment.Payouts ADD cut_off_at_utc DATETIME2 NULL;

    IF COL_LENGTH(N'payment.Payouts', N'payments_received') IS NULL
        ALTER TABLE payment.Payouts ADD payments_received DECIMAL(14,2) NULL;
    IF COL_LENGTH(N'payment.Payouts', N'discounts_given') IS NULL
        ALTER TABLE payment.Payouts ADD discounts_given DECIMAL(14,2) NULL;
    IF COL_LENGTH(N'payment.Payouts', N'refunded_amount') IS NULL
        ALTER TABLE payment.Payouts ADD refunded_amount DECIMAL(14,2) NULL;
    IF COL_LENGTH(N'payment.Payouts', N'net_sales') IS NULL
        ALTER TABLE payment.Payouts ADD net_sales DECIMAL(14,2) NULL;
    IF COL_LENGTH(N'payment.Payouts', N'payable_net_before_adjustments') IS NULL
        ALTER TABLE payment.Payouts ADD payable_net_before_adjustments DECIMAL(14,2) NULL;
    IF COL_LENGTH(N'payment.Payouts', N'adjustments_applied') IS NULL
        ALTER TABLE payment.Payouts ADD adjustments_applied DECIMAL(14,2) NOT NULL CONSTRAINT DF_Payouts_AdjustmentsApplied DEFAULT 0;

    IF COL_LENGTH(N'payment.Payouts', N'booking_count') IS NULL
        ALTER TABLE payment.Payouts ADD booking_count INT NOT NULL CONSTRAINT DF_Payouts_BookingCount DEFAULT 0;
    IF COL_LENGTH(N'payment.Payouts', N'participant_count') IS NULL
        ALTER TABLE payment.Payouts ADD participant_count INT NOT NULL CONSTRAINT DF_Payouts_ParticipantCount DEFAULT 0;
    IF COL_LENGTH(N'payment.Payouts', N'minimum_amount_checked') IS NULL
        ALTER TABLE payment.Payouts ADD minimum_amount_checked DECIMAL(14,2) NULL;

    IF COL_LENGTH(N'payment.Payouts', N'bank_name') IS NULL
        ALTER TABLE payment.Payouts ADD bank_name NVARCHAR(150) NULL;
    IF COL_LENGTH(N'payment.Payouts', N'account_holder_name') IS NULL
        ALTER TABLE payment.Payouts ADD account_holder_name NVARCHAR(150) NULL;
    IF COL_LENGTH(N'payment.Payouts', N'account_number_masked') IS NULL
        ALTER TABLE payment.Payouts ADD account_number_masked VARCHAR(30) NULL;
    IF COL_LENGTH(N'payment.Payouts', N'account_number_protected') IS NULL
        ALTER TABLE payment.Payouts ADD account_number_protected NVARCHAR(500) NULL;

    IF COL_LENGTH(N'payment.Payouts', N'cancelled_at') IS NULL
        ALTER TABLE payment.Payouts ADD cancelled_at DATETIME2 NULL;

    /* Backfill the new NOT-NULL-to-be columns for any pre-existing rows (expected to be
       none on a fresh UC-46 database, but this keeps the migration safe either way). */
    -- SQL Server compiles a batch before executing its ALTER TABLE statements.  Use dynamic
    -- SQL here so this backfill is compiled only after the guarded column additions above.
    EXEC(N'
        UPDATE payment.Payouts
        SET payments_received = ISNULL(payments_received, gross_revenue),
            discounts_given = ISNULL(discounts_given, 0),
            refunded_amount = ISNULL(refunded_amount, 0),
            net_sales = ISNULL(net_sales, gross_revenue),
            payable_net_before_adjustments = ISNULL(payable_net_before_adjustments, net_amount),
            settlement_month = ISNULL(settlement_month, DATEFROMPARTS(YEAR(period_start), MONTH(period_start), 1)),
            payout_code = ISNULL(payout_code, ''PO-LEGACY-'' + RIGHT(''000000'' + CAST(payout_id AS VARCHAR(6)), 6))
        WHERE payments_received IS NULL OR discounts_given IS NULL OR refunded_amount IS NULL
           OR net_sales IS NULL OR payable_net_before_adjustments IS NULL OR settlement_month IS NULL
           OR payout_code IS NULL;');

    DECLARE @sql NVARCHAR(MAX);

    IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'payment.Payouts') AND name = N'UX_Payouts_Code')
        EXEC(N'CREATE UNIQUE INDEX UX_Payouts_Code ON payment.Payouts(payout_code) WHERE payout_code IS NOT NULL;');

    /* ---------- payment.Payouts: replace CHECK constraints with the UC-46 shape ---------- */

    SET @sql = N'';
    SELECT @sql += N'ALTER TABLE payment.Payouts DROP CONSTRAINT [' + cc.name + N'];'
    FROM sys.check_constraints AS cc
    WHERE cc.parent_object_id = OBJECT_ID(N'payment.Payouts')
      AND cc.name IN (
          N'CK_Payouts_NetAmount',
          N'CK_Payouts_Status',
          N'CK_Payouts_LifecycleState',
          N'CK_Payouts_BeneficiarySnapshot');
    IF @sql <> N'' EXEC sp_executesql @sql;

    EXEC(N'ALTER TABLE payment.Payouts ADD CONSTRAINT CK_Payouts_Status CHECK (status IN (
        ''PendingReview'', ''ApprovedForSettlement'', ''SettlementProcessing'', ''Settled'',
        ''SettlementFailed'', ''Rejected'', ''Cancelled''));');

    /* net_amount is retained for UC-63/64/65, but now represents the requested payable amount.
       It must equal the UC-46 snapshot calculation, not the legacy gross-minus-commission
       formula, because discounts, refunds, and applied adjustments are all part of a payout. */
    EXEC(N'ALTER TABLE payment.Payouts ADD CONSTRAINT CK_Payouts_NetAmount CHECK (
        payable_net_before_adjustments IS NULL
        OR net_amount = payable_net_before_adjustments - adjustments_applied);');

    -- The legacy lifecycle constraint refers to the old beneficiary snapshot columns and has
    -- no Cancelled state. UC-46 snapshots the new protected/masked bank fields instead.
    EXEC(N'ALTER TABLE payment.Payouts ADD CONSTRAINT CK_Payouts_LifecycleState CHECK (
        gateway_attempt_count >= 0
        AND (
            (status = ''PendingReview''
                AND reviewed_by IS NULL AND reviewed_at IS NULL AND rejection_reason IS NULL
                AND settlement_authorized_by IS NULL AND settlement_authorized_at IS NULL
                AND gateway_reference IS NULL AND gateway_failure_reason IS NULL
                AND gateway_attempt_count = 0 AND last_gateway_attempt_at IS NULL
                AND cancelled_at IS NULL)
            OR (status = ''Cancelled''
                AND reviewed_by IS NULL AND reviewed_at IS NULL AND rejection_reason IS NULL
                AND settlement_authorized_by IS NULL AND settlement_authorized_at IS NULL
                AND gateway_reference IS NULL AND gateway_failure_reason IS NULL
                AND gateway_attempt_count = 0 AND last_gateway_attempt_at IS NULL
                AND cancelled_at IS NOT NULL)
            OR (status = ''ApprovedForSettlement''
                AND reviewed_by IS NOT NULL AND reviewed_at IS NOT NULL AND rejection_reason IS NULL
                AND settlement_authorized_by IS NULL AND settlement_authorized_at IS NULL
                AND gateway_reference IS NULL AND gateway_failure_reason IS NULL
                AND gateway_attempt_count = 0 AND last_gateway_attempt_at IS NULL
                AND cancelled_at IS NULL)
            OR (status = ''Rejected''
                AND reviewed_by IS NOT NULL AND reviewed_at IS NOT NULL AND rejection_reason IS NOT NULL
                AND settlement_authorized_by IS NULL AND settlement_authorized_at IS NULL
                AND gateway_reference IS NULL AND gateway_failure_reason IS NULL
                AND gateway_attempt_count = 0 AND last_gateway_attempt_at IS NULL
                AND cancelled_at IS NULL)
            OR (status = ''SettlementProcessing''
                AND reviewed_by IS NOT NULL AND reviewed_at IS NOT NULL AND rejection_reason IS NULL
                AND settlement_authorized_by IS NOT NULL AND settlement_authorized_at IS NOT NULL
                AND gateway_failure_reason IS NULL AND cancelled_at IS NULL)
            OR (status = ''Settled''
                AND reviewed_by IS NOT NULL AND reviewed_at IS NOT NULL AND rejection_reason IS NULL
                AND settlement_authorized_by IS NOT NULL AND settlement_authorized_at IS NOT NULL
                AND gateway_reference IS NOT NULL AND gateway_failure_reason IS NULL
                AND gateway_attempt_count >= 1 AND last_gateway_attempt_at IS NOT NULL
                AND cancelled_at IS NULL)
            OR (status = ''SettlementFailed''
                AND reviewed_by IS NOT NULL AND reviewed_at IS NOT NULL AND rejection_reason IS NULL
                AND settlement_authorized_by IS NOT NULL AND settlement_authorized_at IS NOT NULL
                AND gateway_failure_reason IS NOT NULL
                AND gateway_attempt_count >= 1 AND last_gateway_attempt_at IS NOT NULL
                AND cancelled_at IS NULL)
        ));');

    IF NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE parent_object_id = OBJECT_ID(N'payment.Payouts') AND name = N'CK_Payouts_PayableNet')
        EXEC(N'ALTER TABLE payment.Payouts ADD CONSTRAINT CK_Payouts_PayableNet CHECK (
            payable_net_before_adjustments IS NULL
            OR payable_net_before_adjustments - adjustments_applied >= 0);');

    /* ---------- active-request unique index: include SettlementFailed ---------- */

    IF EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'payment.Payouts') AND name = N'UX_Payouts_ActiveOperatorPeriod')
        DROP INDEX UX_Payouts_ActiveOperatorPeriod ON payment.Payouts;

    IF NOT EXISTS (
        SELECT 1 FROM sys.indexes
        WHERE object_id = OBJECT_ID(N'payment.Payouts')
          AND name = N'UX_Payouts_ActiveOperatorMonth')
        EXEC(N'CREATE UNIQUE INDEX UX_Payouts_ActiveOperatorMonth
            ON payment.Payouts(operator_user_id, settlement_month)
            WHERE status IN (''PendingReview'', ''ApprovedForSettlement'', ''SettlementProcessing'', ''SettlementFailed'')
              AND settlement_month IS NOT NULL;');

    /* ---------- payment.PayoutItems: per-payment snapshot columns ---------- */

    IF COL_LENGTH(N'payment.PayoutItems', N'payment_transaction_id') IS NULL
        ALTER TABLE payment.PayoutItems ADD payment_transaction_id BIGINT NULL;
    IF COL_LENGTH(N'payment.PayoutItems', N'discount_amount') IS NULL
        ALTER TABLE payment.PayoutItems ADD discount_amount DECIMAL(12,2) NOT NULL CONSTRAINT DF_PayoutItems_DiscountAmount DEFAULT 0;
    IF COL_LENGTH(N'payment.PayoutItems', N'commission_rate_applied') IS NULL
        ALTER TABLE payment.PayoutItems ADD commission_rate_applied DECIMAL(5,2) NULL;
    IF COL_LENGTH(N'payment.PayoutItems', N'commission_amount') IS NULL
        ALTER TABLE payment.PayoutItems ADD commission_amount DECIMAL(12,2) NOT NULL CONSTRAINT DF_PayoutItems_CommissionAmount DEFAULT 0;

    -- The original schema's "amount" column becomes "paid_amount": same data, clearer name now
    -- that a payout item also carries discount/commission figures alongside it.
    IF COL_LENGTH(N'payment.PayoutItems', N'amount') IS NOT NULL AND COL_LENGTH(N'payment.PayoutItems', N'paid_amount') IS NULL
    BEGIN
        SET @sql = N'';
        SELECT @sql += N'ALTER TABLE payment.PayoutItems DROP CONSTRAINT [' + cc.name + N'];'
        FROM sys.check_constraints AS cc
        WHERE cc.parent_object_id = OBJECT_ID(N'payment.PayoutItems')
          AND cc.definition LIKE N'%amount%';
        IF @sql <> N'' EXEC sp_executesql @sql;

        EXEC sp_rename N'payment.PayoutItems.amount', N'paid_amount', N'COLUMN';
    END;

    IF NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE parent_object_id = OBJECT_ID(N'payment.PayoutItems') AND name = N'CK_PayoutItems_Amount')
        EXEC(N'ALTER TABLE payment.PayoutItems ADD CONSTRAINT CK_PayoutItems_Amount CHECK (paid_amount >= 0);');

    IF NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = N'FK_PayoutItems_PaymentTransaction')
        EXEC(N'ALTER TABLE payment.PayoutItems ADD CONSTRAINT FK_PayoutItems_PaymentTransaction
            FOREIGN KEY (payment_transaction_id) REFERENCES payment.PaymentTransactions(transaction_id);');

    /* ---------- payment.PayoutRefundItems: new table ---------- */

    IF OBJECT_ID(N'payment.PayoutRefundItems', N'U') IS NULL
    BEGIN
        CREATE TABLE payment.PayoutRefundItems (
            payout_refund_item_id  BIGINT IDENTITY(1,1) PRIMARY KEY,
            payout_id              BIGINT NOT NULL REFERENCES payment.Payouts(payout_id) ON DELETE CASCADE,
            refund_id              BIGINT NOT NULL REFERENCES payment.Refunds(refund_id),
            booking_id             BIGINT NOT NULL REFERENCES commerce.Bookings(booking_id),
            refunded_amount        DECIMAL(12,2) NOT NULL CHECK (refunded_amount >= 0),
            commission_rate_applied DECIMAL(5,2) NOT NULL,
            commission_reduction   DECIMAL(12,2) NOT NULL CHECK (commission_reduction >= 0),
            CONSTRAINT UQ_PayoutRefundItems UNIQUE (payout_id, refund_id)
        );
    END;

    /* ---------- payment.PayoutAdjustments: new table (late-refund ledger) ---------- */

    IF OBJECT_ID(N'payment.PayoutAdjustments', N'U') IS NULL
    BEGIN
        CREATE TABLE payment.PayoutAdjustments (
            adjustment_id      BIGINT IDENTITY(1,1) PRIMARY KEY,
            operator_user_id   BIGINT NOT NULL REFERENCES dbo.OperatorProfiles(user_id),
            source_refund_id   BIGINT NOT NULL REFERENCES payment.Refunds(refund_id),
            amount             DECIMAL(12,2) NOT NULL CHECK (amount < 0),
            created_at         DATETIME2 NOT NULL CONSTRAINT DF_PayoutAdjustments_CreatedAt DEFAULT SYSUTCDATETIME(),
            applied_payout_id  BIGINT NULL REFERENCES payment.Payouts(payout_id),
            applied_amount     DECIMAL(12,2) NULL,
            applied_at         DATETIME2 NULL,
            CONSTRAINT UQ_PayoutAdjustments_SourceRefund UNIQUE (source_refund_id),
            CONSTRAINT CK_PayoutAdjustments_AppliedState CHECK (
                (applied_payout_id IS NULL AND applied_amount IS NULL AND applied_at IS NULL)
                OR (applied_payout_id IS NOT NULL AND applied_amount IS NOT NULL AND applied_at IS NOT NULL)
            )
        );
        CREATE INDEX IX_PayoutAdjustments_Operator_Outstanding
            ON payment.PayoutAdjustments(operator_user_id)
            WHERE applied_payout_id IS NULL;
    END;

    /* ---------- dbo.OperatorProfiles: beneficiary bank account ---------- */

    IF COL_LENGTH(N'dbo.OperatorProfiles', N'bank_name') IS NULL
        ALTER TABLE dbo.OperatorProfiles ADD bank_name NVARCHAR(150) NULL;
    IF COL_LENGTH(N'dbo.OperatorProfiles', N'account_holder_name') IS NULL
        ALTER TABLE dbo.OperatorProfiles ADD account_holder_name NVARCHAR(150) NULL;
    IF COL_LENGTH(N'dbo.OperatorProfiles', N'account_number_masked') IS NULL
        ALTER TABLE dbo.OperatorProfiles ADD account_number_masked VARCHAR(30) NULL;
    IF COL_LENGTH(N'dbo.OperatorProfiles', N'account_number_protected') IS NULL
        ALTER TABLE dbo.OperatorProfiles ADD account_number_protected NVARCHAR(500) NULL;
    IF COL_LENGTH(N'dbo.OperatorProfiles', N'bank_account_updated_at') IS NULL
        ALTER TABLE dbo.OperatorProfiles ADD bank_account_updated_at DATETIME2 NULL;

    /* ---------- dbo.SystemConfigs: UC-46 configuration seeds ---------- */

    IF NOT EXISTS (SELECT 1 FROM dbo.SystemConfigs WHERE config_key = N'payout.minimum_amount_vnd')
        INSERT INTO dbo.SystemConfigs (config_key, config_value, description, updated_at)
        VALUES (N'payout.minimum_amount_vnd', N'5000000',
            N'UC-46: minimum payable net amount (VND) for a payout request to be allowed. Production value; lower dev/staging values are a manual config update in that environment only, never an environment check in code.',
            SYSUTCDATETIME());

    IF NOT EXISTS (SELECT 1 FROM dbo.SystemConfigs WHERE config_key = N'payout.refund_hold_days')
        INSERT INTO dbo.SystemConfigs (config_key, config_value, description, updated_at)
        VALUES (N'payout.refund_hold_days', N'3',
            N'UC-46 BE spec OPEN-1 (placeholder pending PO confirmation): number of days after a tour schedule''s end before its completed bookings are considered final enough to include in a payout.',
            SYSUTCDATETIME());

    COMMIT TRANSACTION;
END TRY
BEGIN CATCH
    IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;
    THROW;
END CATCH;
GO
