SET XACT_ABORT ON;
SET NOCOUNT ON;

BEGIN TRY
    BEGIN TRANSACTION;

    DECLARE @constraintName NVARCHAR(256);

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

    IF NOT EXISTS (
        SELECT 1
        FROM sys.check_constraints
        WHERE name = N'CK_Users_Role'
          AND parent_object_id = OBJECT_ID(N'dbo.Users'))
    BEGIN
        ALTER TABLE dbo.Users
            ADD CONSTRAINT CK_Users_Role
            CHECK (role IN ('Traveler','TourOperator','Administrator','Staff'));
    END;

    COMMIT TRANSACTION;
END TRY
BEGIN CATCH
    IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;
    THROW;
END CATCH;
GO
