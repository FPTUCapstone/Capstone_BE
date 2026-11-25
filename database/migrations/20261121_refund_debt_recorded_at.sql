/*
    UC-41/UC-42/UC-67 SRS-alignment (plan Task 8, spec v1.2): BR-144 return-deadline debt
    recording. One new nullable column on payment.Refunds — debt_recorded_at — set once the
    return-deadline scan has recorded a payment.TourOperatorDebt row for a refund's unreturned
    share, so a later scan never records the same debt twice. The refund's own status is
    untouched by this column.

    Idempotent: guarded, so a re-run on an upgraded database is a no-op.
*/
SET ANSI_NULLS ON;
SET ANSI_PADDING ON;
SET ANSI_WARNINGS ON;
SET ARITHABORT ON;
SET CONCAT_NULL_YIELDS_NULL ON;
SET QUOTED_IDENTIFIER ON;
SET NUMERIC_ROUNDABORT OFF;
GO

IF COL_LENGTH(N'payment.Refunds', N'debt_recorded_at') IS NULL
    ALTER TABLE payment.Refunds ADD debt_recorded_at DATETIME2 NULL;
GO
