/*
    UC-34 Update Tour Operator Profile: business-profile fields an approved Tour Operator may edit.
    Five new nullable columns on dbo.OperatorProfiles. Existing rows get NULL and are unaffected.
    Legal identifiers (tax_code, business_license_no), approval state, commission and the UC-46
    bank account are not touched by this script and stay read-only in the API.

    Idempotent: every column is guarded, so a re-run on an upgraded database is a no-op.
*/
SET ANSI_NULLS ON;
SET ANSI_PADDING ON;
SET ANSI_WARNINGS ON;
SET ARITHABORT ON;
SET CONCAT_NULL_YIELDS_NULL ON;
SET QUOTED_IDENTIFIER ON;
SET XACT_ABORT ON;
GO

IF OBJECT_ID(N'dbo.OperatorProfiles', N'U') IS NULL
    THROW 51000, 'UC-34 migration requires dbo.OperatorProfiles.', 1;
GO

IF COL_LENGTH(N'dbo.OperatorProfiles', N'description') IS NULL
    ALTER TABLE dbo.OperatorProfiles ADD description NVARCHAR(2000) NULL;
GO

IF COL_LENGTH(N'dbo.OperatorProfiles', N'contact_email') IS NULL
    ALTER TABLE dbo.OperatorProfiles ADD contact_email NVARCHAR(200) NULL;
GO

IF COL_LENGTH(N'dbo.OperatorProfiles', N'website_url') IS NULL
    ALTER TABLE dbo.OperatorProfiles ADD website_url NVARCHAR(300) NULL;
GO

IF COL_LENGTH(N'dbo.OperatorProfiles', N'logo_url') IS NULL
    ALTER TABLE dbo.OperatorProfiles ADD logo_url NVARCHAR(500) NULL;
GO

IF COL_LENGTH(N'dbo.OperatorProfiles', N'logo_public_id') IS NULL
    ALTER TABLE dbo.OperatorProfiles ADD logo_public_id NVARCHAR(300) NULL;
GO
