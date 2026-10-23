-- UC-02: enforce BR-08 for business licence numbers before the registration API is deployed.
-- Existing duplicate non-empty values require a business decision; this script never edits them.
SET XACT_ABORT ON;

BEGIN TRY
    BEGIN TRANSACTION;

    IF OBJECT_ID(N'dbo.OperatorProfiles', N'U') IS NULL
        THROW 51000, 'UC-02 migration requires dbo.OperatorProfiles.', 1;

    IF COL_LENGTH(N'dbo.OperatorProfiles', N'business_license_no') IS NULL
        THROW 51000, 'UC-02 migration requires dbo.OperatorProfiles.business_license_no.', 1;

    IF EXISTS (
        SELECT 1
        FROM dbo.OperatorProfiles
        WHERE business_license_no <> N''
        GROUP BY business_license_no
        HAVING COUNT(*) > 1)
        THROW 51000,
            'UC-02 migration found duplicate business licence numbers; resolve the existing records before retrying.',
            1;

    IF EXISTS (
        SELECT 1
        FROM sys.indexes
        WHERE object_id = OBJECT_ID(N'dbo.OperatorProfiles')
          AND name = N'UX_OperatorProfiles_BusinessLicenseNo')
    BEGIN
        IF NOT EXISTS (
            SELECT 1
            FROM sys.indexes AS index_info
            INNER JOIN sys.index_columns AS index_column
                ON index_column.object_id = index_info.object_id
                AND index_column.index_id = index_info.index_id
            INNER JOIN sys.columns AS column_info
                ON column_info.object_id = index_column.object_id
                AND column_info.column_id = index_column.column_id
            WHERE index_info.object_id = OBJECT_ID(N'dbo.OperatorProfiles')
              AND index_info.name = N'UX_OperatorProfiles_BusinessLicenseNo'
              AND index_info.is_unique = 1
              AND index_info.has_filter = 1
              AND REPLACE(index_info.filter_definition, N' ', N'') = N'([business_license_no]<>N'''')'
              AND index_column.key_ordinal = 1
              AND column_info.name = N'business_license_no'
              AND (SELECT COUNT(*)
                   FROM sys.index_columns AS all_columns
                   WHERE all_columns.object_id = index_info.object_id
                     AND all_columns.index_id = index_info.index_id) = 1)
            THROW 51000,
                'UC-02 migration found UX_OperatorProfiles_BusinessLicenseNo with an unexpected definition.',
                1;
    END
    ELSE
        CREATE UNIQUE INDEX UX_OperatorProfiles_BusinessLicenseNo
            ON dbo.OperatorProfiles(business_license_no)
            WHERE business_license_no <> N'';

    COMMIT TRANSACTION;
END TRY
BEGIN CATCH
    IF @@TRANCOUNT > 0
        ROLLBACK TRANSACTION;

    THROW;
END CATCH;
