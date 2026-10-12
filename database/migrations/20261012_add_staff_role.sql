/*
    TM-209: Introduce the Staff account role used by internal payout review.
    Existing account rows are preserved; this only expands the allowed role set.
*/
SET XACT_ABORT ON;
GO

BEGIN TRY
    BEGIN TRANSACTION;

    IF OBJECT_ID(N'dbo.Users', N'U') IS NULL
        THROW 51000, 'TM-209 requires dbo.Users.', 1;

    DECLARE @sql NVARCHAR(MAX) = N'';

    SELECT @sql += N'ALTER TABLE dbo.Users DROP CONSTRAINT [' + constraint_info.name + N'];'
    FROM sys.check_constraints AS constraint_info
    WHERE constraint_info.parent_object_id = OBJECT_ID(N'dbo.Users')
      AND constraint_info.definition LIKE N'%role%'
      AND constraint_info.definition LIKE N'%Administrator%';
    IF @sql <> N'' EXEC sp_executesql @sql;

    IF NOT EXISTS (
        SELECT 1
        FROM sys.check_constraints
        WHERE parent_object_id = OBJECT_ID(N'dbo.Users')
          AND name = N'CK_Users_Role')
    BEGIN
        ALTER TABLE dbo.Users ADD CONSTRAINT CK_Users_Role
            CHECK (role IN ('Traveler', 'TourOperator', 'Administrator', 'Staff'));
    END;

    COMMIT TRANSACTION;
END TRY
BEGIN CATCH
    IF XACT_STATE() <> 0 ROLLBACK TRANSACTION;
    THROW;
END CATCH;
GO
