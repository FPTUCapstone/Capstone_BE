-- UC-37: unknown historical submission metadata remains NULL; never backfill from update/review time.
SET XACT_ABORT ON;
BEGIN TRY
    BEGIN TRANSACTION;

    IF OBJECT_ID(N'commerce.Tours', N'U') IS NULL
        THROW 51183, 'UC-37 requires commerce.Tours.', 1;

    -- Same-name columns must already match the canonical nullable storage contract.
    -- Reject drift before any additive change, including reduced datetime2 precision.
    IF EXISTS (
        SELECT 1 FROM sys.columns
        WHERE object_id = OBJECT_ID(N'commerce.Tours') AND name = N'submitted_at_utc'
          AND (user_type_id <> TYPE_ID(N'datetime2') OR scale <> 7 OR is_nullable <> 1
               OR is_computed <> 0 OR default_object_id <> 0)
    )
        THROW 51183, 'UC-37 submitted_at_utc must be nullable datetime2(7) without a default.', 1;

    IF EXISTS (
        SELECT 1 FROM sys.columns
        WHERE object_id = OBJECT_ID(N'commerce.Tours') AND name = N'submission_note'
          AND (user_type_id <> TYPE_ID(N'nvarchar') OR max_length <> 8000 OR is_nullable <> 1
               OR is_computed <> 0 OR default_object_id <> 0)
    )
        THROW 51183, 'UC-37 submission_note must be nullable nvarchar(4000) without a default.', 1;

    IF COL_LENGTH(N'commerce.Tours', N'submitted_at_utc') IS NULL
        ALTER TABLE commerce.Tours ADD submitted_at_utc DATETIME2 NULL;
    IF COL_LENGTH(N'commerce.Tours', N'submission_note') IS NULL
        ALTER TABLE commerce.Tours ADD submission_note NVARCHAR(4000) NULL;

    COMMIT TRANSACTION;
END TRY
BEGIN CATCH
    IF XACT_STATE() <> 0 ROLLBACK TRANSACTION;
    THROW;
END CATCH;
GO
