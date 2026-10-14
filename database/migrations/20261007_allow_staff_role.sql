SET XACT_ABORT ON;
SET NOCOUNT ON;

BEGIN TRY
    BEGIN TRANSACTION;

    DECLARE @constraintName SYSNAME;
    DECLARE @usersObjectId INT;
    DECLARE @roleColumnId INT;

    IF OBJECT_ID(N'dbo.Users', N'U') IS NULL
        THROW 51000, 'Required table dbo.Users does not exist.', 1;

    IF COL_LENGTH(N'dbo.Users', N'role') IS NULL
        THROW 51000, 'Required column dbo.Users.role does not exist.', 1;

    SET @usersObjectId = OBJECT_ID(N'dbo.Users');
    SELECT @roleColumnId = column_id
    FROM sys.columns
    WHERE object_id = @usersObjectId
      AND name = N'role';

    IF EXISTS (
        SELECT 1
        FROM dbo.Users
        WHERE role NOT IN ('Traveler','TourOperator','Administrator','Staff'))
        THROW 51000, 'dbo.Users contains a role outside the canonical role set.', 1;

    IF EXISTS (
        SELECT 1
        FROM sys.check_constraints
        WHERE name = N'CK_Users_Role'
          AND parent_object_id <> @usersObjectId)
        THROW 51000, 'CK_Users_Role already exists on an incompatible table.', 1;

    DECLARE @roleConstraints TABLE (
        object_id INT NOT NULL PRIMARY KEY,
        constraint_name SYSNAME NOT NULL,
        references_other_columns BIT NOT NULL
    );

    INSERT @roleConstraints(object_id, constraint_name, references_other_columns)
    SELECT
        cc.object_id,
        cc.name,
        CONVERT(BIT, CASE WHEN EXISTS (
            SELECT 1
            FROM sys.sql_expression_dependencies AS other_dependency
            WHERE other_dependency.referencing_id = cc.object_id
              AND other_dependency.referenced_id = @usersObjectId
              AND other_dependency.referenced_minor_id > 0
              AND other_dependency.referenced_minor_id <> @roleColumnId
        ) THEN 1 ELSE 0 END)
    FROM sys.check_constraints AS cc
    WHERE cc.parent_object_id = @usersObjectId
      AND (
          cc.parent_column_id = @roleColumnId
          OR EXISTS (
              SELECT 1
              FROM sys.sql_expression_dependencies AS role_dependency
              WHERE role_dependency.referencing_id = cc.object_id
                AND role_dependency.referenced_id = @usersObjectId
                AND role_dependency.referenced_minor_id = @roleColumnId
          )
      );

    IF (SELECT COUNT(*) FROM @roleConstraints) > 1
        THROW 51000, 'dbo.Users.role has multiple CHECK constraints referencing dbo.Users.role; migration cannot classify them safely.', 1;

    IF EXISTS (SELECT 1 FROM @roleConstraints WHERE references_other_columns = 1)
        THROW 51000, 'A CHECK constraint combines dbo.Users.role with other columns; migration cannot replace that business rule safely.', 1;

    SELECT @constraintName = constraint_name
    FROM @roleConstraints;

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
