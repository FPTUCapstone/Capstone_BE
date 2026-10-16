/*
    UC-35: human readable destination text kept with the tour. It is derived from the selected
    catalogue destinations (commerce.TourDestinations stays the source of truth for search).
    Additive and idempotent.
*/
SET XACT_ABORT ON;
GO

BEGIN TRANSACTION;

IF COL_LENGTH(N'commerce.Tours', N'destination_summary') IS NULL
BEGIN
    -- Dynamic SQL: the column does not exist when this batch is compiled, so every statement that
    -- names it must be compiled after the ADD.
    EXEC sys.sp_executesql N'ALTER TABLE commerce.Tours ADD destination_summary NVARCHAR(300) NULL;';
    EXEC sys.sp_executesql N'UPDATE commerce.Tours SET destination_summary = N''Chưa xác định'' WHERE destination_summary IS NULL;';
    EXEC sys.sp_executesql N'ALTER TABLE commerce.Tours ALTER COLUMN destination_summary NVARCHAR(300) NOT NULL;';
END;

-- Writers that predate this column (seeds, other use cases, raw SQL) must keep working, and
-- databases that applied the first version of this script (NOT NULL, no default) are repaired.
IF NOT EXISTS (
    SELECT 1 FROM sys.default_constraints
    WHERE parent_object_id = OBJECT_ID(N'commerce.Tours')
      AND parent_column_id = COLUMNPROPERTY(OBJECT_ID(N'commerce.Tours'), N'destination_summary', 'ColumnId'))
    EXEC sys.sp_executesql N'ALTER TABLE commerce.Tours
        ADD CONSTRAINT DF_Tours_DestinationSummary DEFAULT N'''' FOR destination_summary;';

COMMIT TRANSACTION;
GO
