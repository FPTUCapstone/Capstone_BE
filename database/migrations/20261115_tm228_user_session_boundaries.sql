-- TM-228 / BR-168, BR-175, BR-176. Additive and safe to rerun on fresh/upgraded databases.
SET XACT_ABORT ON;
BEGIN TRANSACTION;
IF COL_LENGTH('dbo.Users', 'status_before_lock') IS NULL
    ALTER TABLE dbo.Users ADD status_before_lock NVARCHAR(24) NULL;
IF COL_LENGTH('dbo.Users', 'session_version') IS NULL
    ALTER TABLE dbo.Users ADD session_version INT NOT NULL CONSTRAINT DF_Users_SessionVersion DEFAULT (0);
-- Role value matches the TM-225 provider contract; this does not create provider accounts.
IF EXISTS (SELECT 1 FROM sys.check_constraints WHERE parent_object_id = OBJECT_ID('dbo.Users') AND name = 'CK_Users_Role'
    AND definition NOT LIKE '%ServiceProvider%')
BEGIN
    ALTER TABLE dbo.Users DROP CONSTRAINT CK_Users_Role;
    ALTER TABLE dbo.Users WITH CHECK ADD CONSTRAINT CK_Users_Role
        CHECK (role IN ('Traveler','TourOperator','Administrator','Staff','ServiceProvider'));
END;
COMMIT TRANSACTION;
