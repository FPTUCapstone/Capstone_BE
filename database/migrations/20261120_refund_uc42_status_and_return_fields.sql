/*
    UC-41/UC-42/UC-67 SRS-alignment (plan Task 4, spec v1.2): schema for the operator-confirmation
    / partner-return refund path.

    - payment.Refunds.status: widens from VARCHAR(20) to VARCHAR(30) and its CHECK constraint
      gains the six new UC-42 values (PendingOperatorConfirmation, AwaitingPartnerReturn,
      ReturnReported, ReturnReceived, Reconciliation, Escalated) alongside the seven existing
      ones. "PendingOperatorConfirmation" is 27 characters, so 30 leaves margin.
    - New nullable columns, all only set while a refund is on the operator-confirmation /
      partner-return path (SRS 3.8.4.3): operator_return_share, operator_confirmed_at,
      return_deadline_at, return_reference, return_reported_at, return_received_at.

    Does not touch post_trip_refund_request_id or any other column added by the unrelated
    post-trip-refund feature already on develop.

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

-- Status: widen the column before the new CHECK constraint can accept the longer values.
IF EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = N'CK_Refunds_Status')
    ALTER TABLE payment.Refunds DROP CONSTRAINT CK_Refunds_Status;
GO
ALTER TABLE payment.Refunds ALTER COLUMN status VARCHAR(30) NOT NULL;
GO
ALTER TABLE payment.Refunds ADD CONSTRAINT CK_Refunds_Status
    CHECK (status IN (
        N'PendingOperatorConfirmation', N'AwaitingPartnerReturn', N'ReturnReported', N'ReturnReceived',
        N'Pending', N'Processing', N'RetryPending', N'ManualReview', N'Processed', N'Failed', N'Rejected',
        N'Reconciliation', N'Escalated'));
GO

-- UC-42 operator-confirmation / partner-return fields (SRS 3.8.4.3, BR-139, BR-144).
IF COL_LENGTH(N'payment.Refunds', N'operator_return_share') IS NULL
    ALTER TABLE payment.Refunds ADD operator_return_share DECIMAL(12,2) NULL;
GO
IF COL_LENGTH(N'payment.Refunds', N'operator_confirmed_at') IS NULL
    ALTER TABLE payment.Refunds ADD operator_confirmed_at DATETIME2 NULL;
GO
IF COL_LENGTH(N'payment.Refunds', N'return_deadline_at') IS NULL
    ALTER TABLE payment.Refunds ADD return_deadline_at DATETIME2 NULL;
GO
IF COL_LENGTH(N'payment.Refunds', N'return_reference') IS NULL
    ALTER TABLE payment.Refunds ADD return_reference NVARCHAR(150) NULL;
GO
IF COL_LENGTH(N'payment.Refunds', N'return_reported_at') IS NULL
    ALTER TABLE payment.Refunds ADD return_reported_at DATETIME2 NULL;
GO
IF COL_LENGTH(N'payment.Refunds', N'return_received_at') IS NULL
    ALTER TABLE payment.Refunds ADD return_received_at DATETIME2 NULL;
GO
