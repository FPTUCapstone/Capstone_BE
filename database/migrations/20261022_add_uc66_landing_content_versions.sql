SET XACT_ABORT ON;
SET QUOTED_IDENTIFIER ON;
BEGIN TRANSACTION;

IF SCHEMA_ID(N'cms') IS NULL EXEC(N'CREATE SCHEMA cms');

IF OBJECT_ID(N'cms.LandingContentVersions', N'U') IS NULL
BEGIN
    CREATE TABLE cms.LandingContentVersions (
        content_version_id BIGINT IDENTITY(1,1) NOT NULL CONSTRAINT PK_LandingContentVersions PRIMARY KEY,
        version_number INT NOT NULL,
        is_published BIT NOT NULL,
        base_version_number INT NULL,
        content_json NVARCHAR(MAX) NOT NULL,
        published_by_user_id BIGINT NULL,
        published_at DATETIME2 NULL,
        updated_by_user_id BIGINT NULL,
        updated_at DATETIME2 NOT NULL CONSTRAINT DF_LandingContentVersions_UpdatedAt DEFAULT SYSUTCDATETIME(),
        row_version ROWVERSION NOT NULL,
        CONSTRAINT CK_LandingContentVersions_State CHECK ((is_published = 1 AND version_number > 0 AND base_version_number IS NULL) OR (is_published = 0 AND version_number = 0 AND base_version_number IS NOT NULL)),
        CONSTRAINT FK_LandingContentVersions_PublishedBy FOREIGN KEY (published_by_user_id) REFERENCES dbo.Users(user_id),
        CONSTRAINT FK_LandingContentVersions_UpdatedBy FOREIGN KEY (updated_by_user_id) REFERENCES dbo.Users(user_id)
    );
    CREATE UNIQUE INDEX UX_LandingContentVersions_Version ON cms.LandingContentVersions(version_number);
    CREATE UNIQUE INDEX UX_LandingContentVersions_SingleDraft ON cms.LandingContentVersions(is_published) WHERE is_published = 0;
    CREATE INDEX IX_LandingContentVersions_Published ON cms.LandingContentVersions(is_published);
END;

IF NOT EXISTS (SELECT 1 FROM cms.LandingContentVersions WHERE is_published = 1)
BEGIN
    INSERT cms.LandingContentVersions (version_number, is_published, content_json, updated_at)
    VALUES (1, 1, N'{"hero":{"headline":"Discover Vietnam","subHeadline":"","backgroundImage":null,"ctaLabel":"Explore tours","ctaTarget":"/tours"},"destinations":[],"featuredTourIds":[],"banner":{"image":null,"link":"","displayStart":"","displayEnd":""}}', SYSUTCDATETIME());
END;

COMMIT TRANSACTION;
