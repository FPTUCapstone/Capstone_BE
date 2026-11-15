/*
    UC-45 fix: a RevenueExports record claimed by the worker (status -> Processing) had no way
    to be recovered if that worker crashed, was redeployed, or was cancelled before reaching
    MarkReady/MarkFailed. It stayed Processing forever, and the FE kept polling a record that
    would never resolve. This migration adds the columns needed to detect and reclaim a stale
    Processing record: processing_started_at (when the current/last attempt was claimed) and
    attempt_count (claims so far, bounding how many times a record is retried before it is
    given up as Failed).
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

    IF OBJECT_ID(N'reporting.RevenueExports', N'U') IS NULL
        THROW 51000, 'This migration requires reporting.RevenueExports (20261020_uc45_revenue_exports.sql).', 1;

    IF NOT EXISTS (
        SELECT 1 FROM sys.columns
        WHERE object_id = OBJECT_ID(N'reporting.RevenueExports') AND name = N'processing_started_at'
    )
    BEGIN
        ALTER TABLE reporting.RevenueExports ADD processing_started_at DATETIME2 NULL;
    END;

    IF NOT EXISTS (
        SELECT 1 FROM sys.columns
        WHERE object_id = OBJECT_ID(N'reporting.RevenueExports') AND name = N'attempt_count'
    )
    BEGIN
        ALTER TABLE reporting.RevenueExports ADD attempt_count INT NOT NULL
            CONSTRAINT DF_RevenueExports_AttemptCount DEFAULT 0;
    END;

    IF NOT EXISTS (
        SELECT 1 FROM sys.indexes WHERE name = N'IX_RevenueExports_Status_Processing'
            AND object_id = OBJECT_ID(N'reporting.RevenueExports')
    )
    BEGIN
        -- Stale-reclaim sweep scan: only Processing rows are candidates.
        CREATE INDEX IX_RevenueExports_Status_Processing
            ON reporting.RevenueExports (processing_started_at)
            WHERE status = 'Processing';
    END;

    COMMIT TRANSACTION;
END TRY
BEGIN CATCH
    IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;
    THROW;
END CATCH;
GO
