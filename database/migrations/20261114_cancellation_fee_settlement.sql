/*
    Cancellation-fee settlement:
    a cancelled, paid booking can create one Cancellation settlement after its refund is final.
    The settlement records the Tour Operator's 90% share of the non-refunded cancellation fee;
    TripMate's 10% share is stored as commission_amount.

    This is additive and idempotent. It relies on the v6 Advance/Final migration having removed
    legacy Main/Retained values first.
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
    SELECT 1
    FROM payment.PayoutSettlement
    WHERE kind NOT IN (N'Advance', N'Final', N'Cancellation'))
BEGIN
    THROW 51003, N'20261114_cancellation_fee_settlement.sql: legacy or unknown payout settlement kinds exist. Apply the v6 archive/schema migrations first.', 1;
END
GO

IF EXISTS (SELECT 1 FROM sys.check_constraints
           WHERE parent_object_id = OBJECT_ID(N'payment.PayoutSettlement')
             AND name = N'CK_PayoutSettlement_kind')
    ALTER TABLE payment.PayoutSettlement DROP CONSTRAINT CK_PayoutSettlement_kind;
GO

ALTER TABLE payment.PayoutSettlement ADD CONSTRAINT CK_PayoutSettlement_kind
    CHECK (kind IN (N'Advance', N'Final', N'Cancellation'));
GO
