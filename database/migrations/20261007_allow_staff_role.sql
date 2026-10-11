SET XACT_ABORT ON;
SET NOCOUNT ON;

BEGIN TRY
    BEGIN TRANSACTION;

    DECLARE @constraintName NVARCHAR(256);

    IF OBJECT_ID(N'dbo.Users', N'U') IS NULL
        THROW 51000, 'Required table dbo.Users does not exist.', 1;

    IF COL_LENGTH(N'dbo.Users', N'role') IS NULL
        THROW 51000, 'Required column dbo.Users.role does not exist.', 1;

    IF EXISTS (
        SELECT 1
        FROM dbo.Users
        WHERE role NOT IN ('Traveler','TourOperator','Administrator','Staff'))
        THROW 51000, 'dbo.Users contains a role outside the canonical role set.', 1;

    IF EXISTS (
        SELECT 1
        FROM sys.check_constraints
        WHERE name = N'CK_Users_Role'
          AND parent_object_id <> OBJECT_ID(N'dbo.Users'))
        THROW 51000, 'CK_Users_Role already exists on an incompatible table.', 1;

    SELECT @constraintName = cc.name
    FROM sys.check_constraints cc
    INNER JOIN sys.columns c
        ON cc.parent_object_id = c.object_id
       AND cc.parent_column_id = c.column_id
    WHERE cc.parent_object_id = OBJECT_ID(N'dbo.Users')
      AND c.name = N'role';

    IF @constraintName IS NOT NULL
    BEGIN
        DECLARE @dropSql NVARCHAR(512) = N'ALTER TABLE dbo.Users DROP CONSTRAINT ' + QUOTENAME(@constraintName) + N';';
        EXEC sp_executesql @dropSql;
    END;

    ALTER TABLE dbo.Users WITH CHECK
        ADD CONSTRAINT CK_Users_Role
        CHECK (role IN ('Traveler','TourOperator','Administrator','Staff'));

    ALTER TABLE dbo.Users CHECK CONSTRAINT CK_Users_Role;

    COMMIT TRANSACTION;
END TRY
BEGIN CATCH
    IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;
    THROW;
END CATCH;
GO
