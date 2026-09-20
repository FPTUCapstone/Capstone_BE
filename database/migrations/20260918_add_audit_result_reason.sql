-- Apply before deploying code that reads/writes Result and Reason.
-- Historical outcomes are deliberately left NULL; no guessed backfill/default.
SET XACT_ABORT ON;
BEGIN TRANSACTION;

IF OBJECT_ID(N'dbo.AuditLogs', N'U') IS NULL
    THROW 50001, 'dbo.AuditLogs must exist before applying this migration.', 1;

IF COL_LENGTH(N'dbo.AuditLogs', N'result') IS NULL
    ALTER TABLE dbo.AuditLogs ADD result VARCHAR(20) NULL;

IF COL_LENGTH(N'dbo.AuditLogs', N'reason') IS NULL
    ALTER TABLE dbo.AuditLogs ADD reason NVARCHAR(1000) NULL;

IF NOT EXISTS (SELECT 1 FROM sys.check_constraints
    WHERE parent_object_id = OBJECT_ID(N'dbo.AuditLogs') AND name = N'CK_AuditLogs_Result')
    EXEC(N'ALTER TABLE dbo.AuditLogs WITH CHECK ADD CONSTRAINT CK_AuditLogs_Result
        CHECK (result IN (''Success'', ''Failure''));');

COMMIT TRANSACTION;
