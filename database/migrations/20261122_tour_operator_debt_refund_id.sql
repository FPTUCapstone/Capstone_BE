/*
    UC-42 SRS-alignment (BR-144): links a payment.TourOperatorDebt row to the refund whose
    unreturned Tour Operator share it was recorded for. Lets the debt be closed when that share
    is later received in full by bank transfer, instead of also being offset against settlements.
    Nullable: debts from any other source keep refund_id NULL.

    Idempotent: guarded, so a re-run on an upgraded database is a no-op.
*/
SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
GO

IF COL_LENGTH(N'payment.TourOperatorDebt', N'refund_id') IS NULL
    ALTER TABLE payment.TourOperatorDebt ADD refund_id BIGINT NULL;
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_TourOperatorDebt_Refund'
               AND object_id = OBJECT_ID(N'payment.TourOperatorDebt'))
    CREATE INDEX IX_TourOperatorDebt_Refund ON payment.TourOperatorDebt (refund_id) WHERE refund_id IS NOT NULL;
GO
