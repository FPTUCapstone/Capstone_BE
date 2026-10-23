/* UC-67: closed-period reporting gates and immutable per-criteria report snapshots.
   This migration is idempotent. Existing completed calendar periods are closed once at rollout;
   later reports persist their result at first generation and exports always reuse that snapshot. */
SET XACT_ABORT ON;
GO
BEGIN TRY
    BEGIN TRANSACTION;

    IF SCHEMA_ID(N'reporting') IS NULL EXEC(N'CREATE SCHEMA reporting');

    IF OBJECT_ID(N'reporting.ClosedStatisticalPeriods', N'U') IS NULL
    BEGIN
        CREATE TABLE reporting.ClosedStatisticalPeriods (
            closed_period_id BIGINT IDENTITY(1,1) NOT NULL CONSTRAINT PK_ClosedStatisticalPeriods PRIMARY KEY,
            period_key VARCHAR(12) NOT NULL,
            start_date DATE NOT NULL,
            end_date DATE NOT NULL,
            closed_at DATETIME2 NOT NULL CONSTRAINT DF_ClosedStatisticalPeriods_ClosedAt DEFAULT SYSUTCDATETIME(),
            closed_by_user_id BIGINT NULL CONSTRAINT FK_ClosedStatisticalPeriods_User REFERENCES dbo.Users(user_id),
            CONSTRAINT CK_ClosedStatisticalPeriods_Range CHECK (end_date >= start_date),
            CONSTRAINT UX_ClosedStatisticalPeriods_PeriodKey UNIQUE (period_key)
        );
    END;

    IF OBJECT_ID(N'reporting.StatisticalReportSnapshots', N'U') IS NULL
    BEGIN
        CREATE TABLE reporting.StatisticalReportSnapshots (
            snapshot_id BIGINT IDENTITY(1,1) NOT NULL CONSTRAINT PK_StatisticalReportSnapshots PRIMARY KEY,
            criteria_key VARCHAR(128) NOT NULL,
            period_key VARCHAR(12) NOT NULL,
            report_type VARCHAR(32) NOT NULL,
            booking_type VARCHAR(24) NOT NULL,
            operator_user_id BIGINT NULL CONSTRAINT FK_StatisticalReportSnapshots_Operator REFERENCES dbo.Users(user_id),
            destination_id BIGINT NULL CONSTRAINT FK_StatisticalReportSnapshots_Destination REFERENCES catalog.Destinations(destination_id),
            payload_json NVARCHAR(MAX) NOT NULL,
            generated_at DATETIME2 NOT NULL CONSTRAINT DF_StatisticalReportSnapshots_GeneratedAt DEFAULT SYSUTCDATETIME(),
            CONSTRAINT UX_StatisticalReportSnapshots_CriteriaKey UNIQUE (criteria_key)
        );
        CREATE INDEX IX_StatisticalReportSnapshots_Period ON reporting.StatisticalReportSnapshots(period_key, report_type);
    END;

    IF OBJECT_ID(N'reporting.StatisticalReportExportOperations', N'U') IS NULL
    BEGIN
        CREATE TABLE reporting.StatisticalReportExportOperations (
            export_operation_id BIGINT IDENTITY(1,1) NOT NULL CONSTRAINT PK_StatisticalReportExportOperations PRIMARY KEY,
            operation_id UNIQUEIDENTIFIER NOT NULL,
            criteria_key VARCHAR(128) NOT NULL,
            format VARCHAR(8) NOT NULL,
            requested_by_user_id BIGINT NOT NULL CONSTRAINT FK_StatisticalReportExportOperations_User REFERENCES dbo.Users(user_id),
            requested_at DATETIME2 NOT NULL CONSTRAINT DF_StatisticalReportExportOperations_RequestedAt DEFAULT SYSUTCDATETIME(),
            CONSTRAINT CK_StatisticalReportExportOperations_Format CHECK (format IN ('csv', 'xlsx')),
            CONSTRAINT UX_StatisticalReportExportOperations_OperationId UNIQUE (operation_id)
        );
    END;
    /* Backfill only finished calendar months, quarters, and years. They become read-only reporting
       periods at rollout. Current and future periods remain unavailable until an authorized close
       operation is introduced; this migration never marks an open period as closed. */
    DECLARE @today DATE = CONVERT(date, SYSUTCDATETIME() AT TIME ZONE 'UTC' AT TIME ZONE 'SE Asia Standard Time');
    DECLARE @month DATE = '2024-01-01';
    WHILE @month < DATEFROMPARTS(YEAR(@today), MONTH(@today), 1)
    BEGIN
        DECLARE @monthEnd DATE = EOMONTH(@month);
        DECLARE @monthKey VARCHAR(12) = CONVERT(char(7), @month, 126);
        IF NOT EXISTS (SELECT 1 FROM reporting.ClosedStatisticalPeriods WHERE period_key = @monthKey)
            INSERT reporting.ClosedStatisticalPeriods(period_key, start_date, end_date) VALUES (@monthKey, @month, @monthEnd);
        SET @month = DATEADD(month, 1, @month);
    END;

    DECLARE @year INT = 2024;
    WHILE @year < YEAR(@today)
    BEGIN
        DECLARE @yearKey VARCHAR(12) = CONVERT(varchar(4), @year);
        IF NOT EXISTS (SELECT 1 FROM reporting.ClosedStatisticalPeriods WHERE period_key = @yearKey)
            INSERT reporting.ClosedStatisticalPeriods(period_key, start_date, end_date)
            VALUES (@yearKey, DATEFROMPARTS(@year, 1, 1), DATEFROMPARTS(@year, 12, 31));
        SET @year += 1;
    END;

    DECLARE @quarter DATE = '2024-01-01';
    WHILE @quarter < DATEFROMPARTS(YEAR(@today), ((MONTH(@today)-1)/3)*3+1, 1)
    BEGIN
        DECLARE @quarterNo INT = DATEPART(quarter, @quarter);
        DECLARE @quarterKey VARCHAR(12) = CONCAT(YEAR(@quarter), '-Q', @quarterNo);
        IF NOT EXISTS (SELECT 1 FROM reporting.ClosedStatisticalPeriods WHERE period_key = @quarterKey)
            INSERT reporting.ClosedStatisticalPeriods(period_key, start_date, end_date)
            VALUES (@quarterKey, @quarter, DATEADD(day, -1, DATEADD(quarter, 1, @quarter)));
        SET @quarter = DATEADD(quarter, 1, @quarter);
    END;

    COMMIT TRANSACTION;
END TRY
BEGIN CATCH
    IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;
    THROW;
END CATCH;
GO
