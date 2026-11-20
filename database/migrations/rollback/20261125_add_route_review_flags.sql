-- Guarded rollback: never discard review flag data without roll-forward recovery.
SET XACT_ABORT ON;
SET NOCOUNT ON;
BEGIN TRY
    BEGIN TRANSACTION;
    IF COL_LENGTH(N'catalog.RouteSegments', N'requires_review') IS NOT NULL
        EXEC(N'IF EXISTS (SELECT 1 FROM catalog.RouteSegments WITH (UPDLOCK,HOLDLOCK)
            WHERE requires_review = 1 OR review_flagged_at_utc IS NOT NULL OR review_flag_reason IS NOT NULL)
            THROW 51000, ''Rollback refused: preserving route review flag data requires roll-forward recovery.'', 1;');

    IF EXISTS (SELECT 1 FROM sys.default_constraints WHERE name = N'DF_RouteSegments_RequiresReview' AND parent_object_id = OBJECT_ID(N'catalog.RouteSegments'))
        ALTER TABLE catalog.RouteSegments DROP CONSTRAINT DF_RouteSegments_RequiresReview;

    IF COL_LENGTH(N'catalog.RouteSegments', N'review_flag_reason') IS NOT NULL
        ALTER TABLE catalog.RouteSegments DROP COLUMN review_flag_reason;

    IF COL_LENGTH(N'catalog.RouteSegments', N'review_flagged_at_utc') IS NOT NULL
        ALTER TABLE catalog.RouteSegments DROP COLUMN review_flagged_at_utc;

    IF COL_LENGTH(N'catalog.RouteSegments', N'requires_review') IS NOT NULL
        ALTER TABLE catalog.RouteSegments DROP COLUMN requires_review;

    COMMIT TRANSACTION;
END TRY
BEGIN CATCH
    IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;
    THROW;
END CATCH;
