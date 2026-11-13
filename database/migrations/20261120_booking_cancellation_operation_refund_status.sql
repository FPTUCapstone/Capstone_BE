/*
    UC-41/UC-42/UC-67 SRS-alignment (plan Task 5, spec v1.2): freezes the refund outcome actually
    recorded at cancellation time onto commerce.BookingCancellationOperations, so an idempotent
    replay (BR-138) reports the original outcome rather than re-deriving it from refund_amount
    alone (which can no longer distinguish "NotRequired" / "PendingOperatorConfirmation" /
    "Reconciliation" — all three now share the same "is it > 0" shape since UC-41 6.a1/6.b1 no
    longer blocks the cancellation).

    Backfill for the 3 existing rows: a zero refund_amount backfills to 'NotRequired'; a non-zero
    one backfills from the linked payment.Refunds row's current status (there is no better
    historical record), defaulting to 'NotRequired' if none is linked.

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

IF COL_LENGTH(N'commerce.BookingCancellationOperations', N'refund_status') IS NULL
    ALTER TABLE commerce.BookingCancellationOperations ADD refund_status VARCHAR(30) NULL;
GO

UPDATE o
SET refund_status = CASE
    WHEN o.refund_amount = 0 THEN N'NotRequired'
    ELSE COALESCE(
        (SELECT TOP 1 r.status FROM payment.Refunds r
         WHERE r.cancellation_operation_id = o.operation_id
         ORDER BY r.refund_id DESC),
        N'NotRequired')
    END
FROM commerce.BookingCancellationOperations o
WHERE o.refund_status IS NULL;
GO

ALTER TABLE commerce.BookingCancellationOperations ALTER COLUMN refund_status VARCHAR(30) NOT NULL;
GO
