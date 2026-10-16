/*
    UC-35/36: preserve an approved/published tour as an immutable revision and
    create a linked draft for every later operator change. Existing data becomes v1.
*/
SET XACT_ABORT ON;
GO

BEGIN TRANSACTION;

IF COL_LENGTH(N'commerce.Tours', N'version_root_tour_id') IS NULL
    EXEC sys.sp_executesql N'ALTER TABLE commerce.Tours ADD version_root_tour_id BIGINT NULL;';

IF COL_LENGTH(N'commerce.Tours', N'version_no') IS NULL
    EXEC sys.sp_executesql N'ALTER TABLE commerce.Tours ADD version_no INT NOT NULL CONSTRAINT DF_Tours_VersionNo DEFAULT (1);';

EXEC sys.sp_executesql N'UPDATE commerce.Tours
SET version_root_tour_id = tour_id,
    version_no = ISNULL(version_no, 1)
WHERE version_root_tour_id IS NULL;';

IF NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = N'FK_Tours_VersionRoot')
    EXEC sys.sp_executesql N'ALTER TABLE commerce.Tours ADD CONSTRAINT FK_Tours_VersionRoot
        FOREIGN KEY (version_root_tour_id) REFERENCES commerce.Tours(tour_id);';

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'commerce.Tours') AND name = N'UX_Tours_VersionLineage')
    EXEC sys.sp_executesql N'CREATE UNIQUE INDEX UX_Tours_VersionLineage ON commerce.Tours(version_root_tour_id, version_no)
        WHERE version_root_tour_id IS NOT NULL;';

COMMIT TRANSACTION;
GO
