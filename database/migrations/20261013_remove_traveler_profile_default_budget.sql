SET XACT_ABORT ON;
BEGIN TRANSACTION;

BEGIN TRY
    IF OBJECT_ID(N'dbo.TravelerProfiles', N'U') IS NULL
        THROW 51013, 'dbo.TravelerProfiles is required before removing the legacy default budget.', 1;

    IF COL_LENGTH(N'dbo.TravelerProfiles', N'default_budget') IS NOT NULL
    BEGIN
        DECLARE @defaultConstraintName sysname;
        SELECT @defaultConstraintName = dc.name
        FROM sys.default_constraints AS dc
        INNER JOIN sys.columns AS c
            ON c.object_id = dc.parent_object_id
            AND c.column_id = dc.parent_column_id
        WHERE dc.parent_object_id = OBJECT_ID(N'dbo.TravelerProfiles')
            AND c.name = N'default_budget';

        IF @defaultConstraintName IS NOT NULL
        BEGIN
            DECLARE @dropDefaultConstraintSql nvarchar(max) =
                N'ALTER TABLE dbo.TravelerProfiles DROP CONSTRAINT '
                + QUOTENAME(@defaultConstraintName) + N';';
            EXEC sys.sp_executesql @dropDefaultConstraintSql;
        END

        ALTER TABLE dbo.TravelerProfiles DROP COLUMN default_budget;
    END

    COMMIT TRANSACTION;
END TRY
BEGIN CATCH
    IF XACT_STATE() <> 0
        ROLLBACK TRANSACTION;
    THROW;
END CATCH;
