SET XACT_ABORT ON;
BEGIN TRANSACTION;

BEGIN TRY
    IF OBJECT_ID(N'dbo.TravelerProfiles', N'U') IS NULL
        THROW 51013, 'dbo.TravelerProfiles is required before removing the legacy default budget.', 1;

    IF COL_LENGTH(N'dbo.TravelerProfiles', N'default_budget') IS NOT NULL
        ALTER TABLE dbo.TravelerProfiles DROP COLUMN default_budget;

    COMMIT TRANSACTION;
END TRY
BEGIN CATCH
    IF XACT_STATE() <> 0
        ROLLBACK TRANSACTION;
    THROW;
END CATCH;
