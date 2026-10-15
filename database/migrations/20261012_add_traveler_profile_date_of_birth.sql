SET XACT_ABORT ON;
BEGIN TRANSACTION;

IF OBJECT_ID(N'dbo.TravelerProfiles', N'U') IS NULL
    THROW 51000, 'UC-08 requires dbo.TravelerProfiles.', 1;

IF COL_LENGTH(N'dbo.TravelerProfiles', N'date_of_birth') IS NULL
    ALTER TABLE dbo.TravelerProfiles ADD date_of_birth DATE NULL;

COMMIT TRANSACTION;