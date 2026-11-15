-- TM-89 / UC-43 BE-03: idempotent SQL projection identity for durable rejection outcomes.
-- Historical audit rows deliberately remain NULL. Apply through the database-first workflow.
SET XACT_ABORT ON;

BEGIN TRY
    BEGIN TRANSACTION;

    IF OBJECT_ID(N'dbo.AuditLogs', N'U') IS NULL
        THROW 50001, 'dbo.AuditLogs must exist before applying this migration.', 1;

    -- Reject drift before making any change. COL_LENGTH alone cannot distinguish a missing
    -- column from an existing column whose type is incompatible with the application model.
    IF EXISTS
    (
        SELECT 1
        FROM sys.columns c
        INNER JOIN sys.types t ON c.user_type_id = t.user_type_id
        WHERE c.object_id = OBJECT_ID(N'dbo.AuditLogs')
          AND c.name = N'scan_attempt_id'
          AND (t.name <> N'uniqueidentifier' OR c.max_length <> 16 OR c.is_nullable <> 1)
    )
        THROW 50002, 'dbo.AuditLogs.scan_attempt_id has an incompatible definition.', 1;

    IF EXISTS
    (
        SELECT 1
        FROM sys.columns c
        INNER JOIN sys.types t ON c.user_type_id = t.user_type_id
        WHERE c.object_id = OBJECT_ID(N'dbo.AuditLogs')
          AND c.name = N'journal_event_id'
          AND (t.name <> N'uniqueidentifier' OR c.max_length <> 16 OR c.is_nullable <> 1)
    )
        THROW 50003, 'dbo.AuditLogs.journal_event_id has an incompatible definition.', 1;

    IF EXISTS
    (
        SELECT 1
        FROM sys.indexes i
        WHERE i.object_id = OBJECT_ID(N'dbo.AuditLogs')
          AND i.name = N'UX_AuditLogs_JournalEventId'
          AND
          (
              i.is_unique <> 1
              OR i.has_filter <> 1
              OR REPLACE(REPLACE(REPLACE(i.filter_definition, N' ', N''), N'(', N''), N')', N'')
                    <> N'[journal_event_id]ISNOTNULL'
              OR (SELECT COUNT(*) FROM sys.index_columns ic
                  WHERE ic.object_id = i.object_id AND ic.index_id = i.index_id
                    AND ic.is_included_column = 0) <> 1
              OR NOT EXISTS
              (
                  SELECT 1
                  FROM sys.index_columns ic
                  INNER JOIN sys.columns c
                      ON c.object_id = ic.object_id AND c.column_id = ic.column_id
                  WHERE ic.object_id = i.object_id AND ic.index_id = i.index_id
                    AND ic.key_ordinal = 1 AND ic.is_included_column = 0
                    AND c.name = N'journal_event_id'
              )
          )
    )
        THROW 50004, 'UX_AuditLogs_JournalEventId exists with an incompatible definition.', 1;

    IF COL_LENGTH(N'dbo.AuditLogs', N'scan_attempt_id') IS NULL
        ALTER TABLE dbo.AuditLogs ADD scan_attempt_id UNIQUEIDENTIFIER NULL;

    IF COL_LENGTH(N'dbo.AuditLogs', N'journal_event_id') IS NULL
        ALTER TABLE dbo.AuditLogs ADD journal_event_id UNIQUEIDENTIFIER NULL;

    IF NOT EXISTS
    (
        SELECT 1
        FROM sys.indexes
        WHERE object_id = OBJECT_ID(N'dbo.AuditLogs')
          AND name = N'UX_AuditLogs_JournalEventId'
    )
    BEGIN
        -- Dynamic SQL is required because SQL Server compiles the complete batch before the
        -- ALTER TABLE statements above have made the new column visible to name resolution.
        DECLARE @hasDuplicateJournalEventId BIT = 0;
        EXEC sys.sp_executesql
            N'IF EXISTS
              (
                  SELECT journal_event_id
                  FROM dbo.AuditLogs
                  WHERE journal_event_id IS NOT NULL
                  GROUP BY journal_event_id
                  HAVING COUNT_BIG(*) > 1
              )
                  SET @hasDuplicates = 1;',
            N'@hasDuplicates BIT OUTPUT',
            @hasDuplicates = @hasDuplicateJournalEventId OUTPUT;

        IF @hasDuplicateJournalEventId = 1
            THROW 50005, 'Duplicate journal_event_id values prevent creation of the unique index.', 1;

        EXEC sys.sp_executesql
            N'CREATE UNIQUE INDEX UX_AuditLogs_JournalEventId
              ON dbo.AuditLogs(journal_event_id)
              WHERE journal_event_id IS NOT NULL;';
    END;

    COMMIT TRANSACTION;
END TRY
BEGIN CATCH
    IF XACT_STATE() <> 0
        ROLLBACK TRANSACTION;
    THROW;
END CATCH;
