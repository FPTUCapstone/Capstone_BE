/*
    UC-45: durable export record, export-event audit trail, and worker/expiry metadata for
    the Export Revenue Report feature. The filter snapshot columns (start_date..tour_id, format,
    scope) are written once at creation and never updated (BR-110): retrying or downloading an
    export always regenerates from this persisted snapshot, never from the caller's current UI
    filters. commerce.Tours and dbo.Users already exist (tripmate_schema_v7.sql); this migration
    only adds the new reporting.RevenueExports table.
*/
-- Filtered indexes below require these session options.  Do not rely on the
-- caller (sqlcmd, Docker init, or a deployment runner) to provide them.
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

    IF OBJECT_ID(N'commerce.Tours', N'U') IS NULL
        THROW 51000, 'UC-45 requires commerce.Tours.', 1;
    IF OBJECT_ID(N'dbo.Users', N'U') IS NULL
        THROW 51000, 'UC-45 requires dbo.Users.', 1;

    IF SCHEMA_ID(N'reporting') IS NULL
        EXEC(N'CREATE SCHEMA reporting');

    IF OBJECT_ID(N'reporting.RevenueExports', N'U') IS NULL
    BEGIN
        CREATE TABLE reporting.RevenueExports (
            export_id BIGINT IDENTITY(1,1) NOT NULL
                CONSTRAINT PK_RevenueExports PRIMARY KEY,
            operator_user_id BIGINT NOT NULL,
            start_date DATE NOT NULL,
            end_date DATE NOT NULL,
            granularity VARCHAR(10) NOT NULL,
            tour_id BIGINT NULL,
            format VARCHAR(10) NOT NULL,
            scope VARCHAR(20) NOT NULL,
            status VARCHAR(12) NOT NULL,
            requested_at DATETIME2 NOT NULL
                CONSTRAINT DF_RevenueExports_RequestedAt DEFAULT SYSUTCDATETIME(),
            completed_at DATETIME2 NULL,
            expires_at DATETIME2 NULL,
            row_count INT NULL,
            file_name NVARCHAR(255) NULL,
            storage_key NVARCHAR(500) NULL,
            failure_code NVARCHAR(100) NULL,
            row_version ROWVERSION NOT NULL,
            CONSTRAINT FK_RevenueExports_Operator FOREIGN KEY (operator_user_id)
                REFERENCES dbo.Users(user_id),
            CONSTRAINT FK_RevenueExports_Tour FOREIGN KEY (tour_id)
                REFERENCES commerce.Tours(tour_id),
            CONSTRAINT CK_RevenueExports_DateRange CHECK (end_date >= start_date),
            CONSTRAINT CK_RevenueExports_Granularity CHECK (granularity IN ('daily', 'weekly', 'monthly')),
            CONSTRAINT CK_RevenueExports_Format CHECK (format IN ('Xlsx', 'Csv', 'Pdf')),
            CONSTRAINT CK_RevenueExports_Scope CHECK (scope IN ('Summary', 'SummaryAndDetails')),
            CONSTRAINT CK_RevenueExports_Status CHECK (status IN ('Queued', 'Processing', 'Ready', 'Failed', 'Expired'))
        );

        CREATE INDEX IX_RevenueExports_Operator
            ON reporting.RevenueExports (operator_user_id, requested_at DESC);

        -- Worker claim scan: only Queued rows are candidates.
        CREATE INDEX IX_RevenueExports_Status_Queued
            ON reporting.RevenueExports (status)
            WHERE status = 'Queued';

        -- Expiry cleanup scan: only Ready rows are candidates.
        CREATE INDEX IX_RevenueExports_Expiry_Ready
            ON reporting.RevenueExports (expires_at)
            WHERE status = 'Ready';
    END;

    COMMIT TRANSACTION;
END TRY
BEGIN CATCH
    IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;
    THROW;
END CATCH;
GO
