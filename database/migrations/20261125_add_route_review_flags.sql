-- TM-100 / Owner Decision D3: Additive migration for route review flags when connecting POIs are deactivated.
-- Safe to repeat; preserves all existing route data.
SET XACT_ABORT ON;
SET NOCOUNT ON;
BEGIN TRY
    BEGIN TRANSACTION;
    IF OBJECT_ID(N'catalog.RouteSegments', N'U') IS NULL
        THROW 51000, 'Required catalog.RouteSegments table is missing.', 1;

    IF COL_LENGTH(N'catalog.RouteSegments', N'requires_review') IS NULL
    BEGIN
        ALTER TABLE catalog.RouteSegments ADD requires_review BIT NOT NULL
            CONSTRAINT DF_RouteSegments_RequiresReview DEFAULT (0) WITH VALUES;
    END;

    IF COL_LENGTH(N'catalog.RouteSegments', N'review_flagged_at_utc') IS NULL
    BEGIN
        ALTER TABLE catalog.RouteSegments ADD review_flagged_at_utc DATETIME2 NULL;
    END;

    IF COL_LENGTH(N'catalog.RouteSegments', N'review_flag_reason') IS NULL
    BEGIN
        ALTER TABLE catalog.RouteSegments ADD review_flag_reason NVARCHAR(500) NULL;
    END;

    COMMIT TRANSACTION;
END TRY
BEGIN CATCH
    IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;
    THROW;
END CATCH;
