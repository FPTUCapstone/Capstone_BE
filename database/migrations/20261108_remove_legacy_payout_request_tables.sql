/*
    DAILY-PAYOUT-TRANSFER: remove the old operator payout-request/Staff-review tables.

    SRS v11 replaced UC-46 (operator-initiated payout request) and UC-63/64/73/74 (Staff review,
    approve, reject) with the automatic daily settlement run (payment.PayoutRun/PayoutBatch/
    PayoutSettlement, see 20261108_daily_payout_settlement_foundation.sql). Task 1 of this branch
    already removed the write-side handlers (request/cancel/approve/reject); this migration
    removes what Task 1 left behind on the read side once nothing called it any more — the FE's
    UC-46 screens were deleted on the matching FE branch, and no new-model code reads these
    tables.

    Dropped (children before parents):
    - payment.PayoutRefundItems
    - payment.PayoutPeriodBreakdowns
    - payment.PayoutItems
    - payment.Payouts
    The FK from payment.PayoutAdjustments.applied_payout_id to payment.Payouts is dropped first;
    payment.PayoutAdjustments itself is NOT touched — it belongs to the unrelated manual-refund
    adjustment flow and is still in active use.

    Also removes the two dbo.SystemConfigs rows (payout.minimum_amount_vnd, payout.refund_hold_days)
    that only the removed request flow ever read.

    Non-destructive to anything still in use; idempotent (every DROP is existence-guarded, so
    re-running this script, including after it has already run, is a safe no-op). Not reversible —
    see the inventory doc (plans/DAILY-PAYOUT-TRANSFER-be-inventory.md) for the developer
    confirmation this followed; if a dev/staging database holds payout rows worth keeping, export
    them before applying this script there.
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

    DECLARE @sql NVARCHAR(MAX);

    /* ---------- drop the FK from PayoutAdjustments into Payouts (table stays) ---------- */

    IF OBJECT_ID(N'payment.PayoutAdjustments', N'U') IS NOT NULL AND OBJECT_ID(N'payment.Payouts', N'U') IS NOT NULL
    BEGIN
        SET @sql = N'';
        SELECT @sql += N'ALTER TABLE payment.PayoutAdjustments DROP CONSTRAINT [' + foreign_key.name + N'];'
        FROM sys.foreign_keys AS foreign_key
        WHERE foreign_key.parent_object_id = OBJECT_ID(N'payment.PayoutAdjustments')
          AND foreign_key.referenced_object_id = OBJECT_ID(N'payment.Payouts');
        IF @sql <> N'' EXEC sp_executesql @sql;
    END;

    /* ---------- drop the old request-model tables, children first ---------- */

    IF OBJECT_ID(N'payment.PayoutRefundItems', N'U') IS NOT NULL
        DROP TABLE payment.PayoutRefundItems;

    IF OBJECT_ID(N'payment.PayoutPeriodBreakdowns', N'U') IS NOT NULL
        DROP TABLE payment.PayoutPeriodBreakdowns;

    IF OBJECT_ID(N'payment.PayoutItems', N'U') IS NOT NULL
        DROP TABLE payment.PayoutItems;

    IF OBJECT_ID(N'payment.Payouts', N'U') IS NOT NULL
        DROP TABLE payment.Payouts;

    /* ---------- drop config seeds only the removed request flow ever read ---------- */

    DELETE FROM dbo.SystemConfigs WHERE config_key IN (N'payout.minimum_amount_vnd', N'payout.refund_hold_days');

    COMMIT TRANSACTION;
END TRY
BEGIN CATCH
    IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;
    THROW;
END CATCH;
GO
