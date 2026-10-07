/* TM-95 / UC-49: safe administrator account unlock persistence. */
SET XACT_ABORT ON;
GO

BEGIN TRY
    BEGIN TRANSACTION;

    IF OBJECT_ID(N'dbo.Users', N'U') IS NULL
        THROW 51000, 'TM-95 migration requires dbo.Users.', 1;

    IF SCHEMA_ID(N'admin') IS NULL
        EXEC(N'CREATE SCHEMA admin AUTHORIZATION dbo');

    IF COL_LENGTH(N'dbo.Users', N'status_before_lock') IS NULL
        ALTER TABLE dbo.Users ADD status_before_lock VARCHAR(24) NULL;
    IF COL_LENGTH(N'dbo.Users', N'locked_by_user_id') IS NULL
        ALTER TABLE dbo.Users ADD locked_by_user_id BIGINT NULL;
    IF COL_LENGTH(N'dbo.Users', N'locked_at_utc') IS NULL
        ALTER TABLE dbo.Users ADD locked_at_utc DATETIME2 NULL;
    IF COL_LENGTH(N'dbo.Users', N'lock_reason') IS NULL
        ALTER TABLE dbo.Users ADD lock_reason NVARCHAR(1000) NULL;

    IF EXISTS (
        SELECT 1
        FROM sys.columns AS columnDefinition
        WHERE columnDefinition.object_id = OBJECT_ID(N'dbo.Users')
          AND (
              (columnDefinition.name = N'status_before_lock'
                  AND (columnDefinition.system_type_id <> TYPE_ID(N'varchar')
                       OR columnDefinition.max_length <> 24 OR columnDefinition.is_nullable <> 1))
              OR (columnDefinition.name = N'locked_by_user_id'
                  AND (columnDefinition.system_type_id <> TYPE_ID(N'bigint')
                       OR columnDefinition.is_nullable <> 1))
              OR (columnDefinition.name = N'locked_at_utc'
                  AND (columnDefinition.system_type_id <> TYPE_ID(N'datetime2')
                       OR columnDefinition.is_nullable <> 1))
              OR (columnDefinition.name = N'lock_reason'
                  AND (columnDefinition.system_type_id <> TYPE_ID(N'nvarchar')
                       OR columnDefinition.max_length <> 2000 OR columnDefinition.is_nullable <> 1))
          ))
        THROW 51000, 'TM-95 Users lock-recovery column contract mismatch.', 1;

    -- Historical locked accounts cannot be safely restored without all four values. Normalize
    -- partial legacy metadata to the explicit all-null legacy state rather than inventing Active.
    EXEC sys.sp_executesql N'
        UPDATE dbo.Users
        SET status_before_lock = NULL,
            locked_by_user_id = NULL,
            locked_at_utc = NULL,
            lock_reason = NULL
        WHERE status <> ''Locked''
           OR (status = ''Locked'' AND (
                status_before_lock IS NULL
                OR status_before_lock NOT IN (''PendingEmailVerification'', ''Active'', ''PendingApproval'', ''Rejected'', ''Inactive'')
                OR locked_by_user_id IS NULL OR locked_at_utc IS NULL
                OR lock_reason IS NULL OR LEN(LTRIM(RTRIM(lock_reason))) = 0));';

    IF OBJECT_ID(N'dbo.CK_Users_LockRecoveryState', N'C') IS NOT NULL
        ALTER TABLE dbo.Users DROP CONSTRAINT CK_Users_LockRecoveryState;
    EXEC sys.sp_executesql N'
        ALTER TABLE dbo.Users ADD CONSTRAINT CK_Users_LockRecoveryState CHECK (
            (status <> ''Locked'' AND status_before_lock IS NULL AND locked_by_user_id IS NULL
                AND locked_at_utc IS NULL AND lock_reason IS NULL)
            OR (status = ''Locked'' AND (
                (status_before_lock IS NULL AND locked_by_user_id IS NULL AND locked_at_utc IS NULL AND lock_reason IS NULL)
            OR (status_before_lock IN (''PendingEmailVerification'', ''Active'', ''PendingApproval'', ''Rejected'', ''Inactive'')
                AND locked_by_user_id IS NOT NULL AND locked_at_utc IS NOT NULL
                    AND lock_reason IS NOT NULL AND LEN(LTRIM(RTRIM(lock_reason))) > 0)
            ))
        );';

    IF OBJECT_ID(N'dbo.FK_Users_LockedBy', N'F') IS NOT NULL
        ALTER TABLE dbo.Users DROP CONSTRAINT FK_Users_LockedBy;
    EXEC sys.sp_executesql N'
        ALTER TABLE dbo.Users ADD CONSTRAINT FK_Users_LockedBy FOREIGN KEY (locked_by_user_id)
            REFERENCES dbo.Users(user_id);';

    IF OBJECT_ID(N'admin.UserUnlockOperations', N'U') IS NULL
    BEGIN
        CREATE TABLE admin.UserUnlockOperations (
            operation_id BIGINT IDENTITY(1,1) PRIMARY KEY,
            administrator_user_id BIGINT NOT NULL,
            idempotency_key VARCHAR(128) NOT NULL,
            request_hash VARCHAR(128) NOT NULL,
            target_user_id BIGINT NOT NULL,
            restored_status VARCHAR(24) NOT NULL,
            unlocked_at_utc DATETIME2 NOT NULL,
            CONSTRAINT CK_UserUnlockOperations_RestoredStatus CHECK (
                restored_status IN ('PendingEmailVerification','Active','PendingApproval','Rejected','Inactive')
            ),
            CONSTRAINT FK_UserUnlockOperations_Administrator FOREIGN KEY (administrator_user_id)
                REFERENCES dbo.Users(user_id),
            CONSTRAINT FK_UserUnlockOperations_Target FOREIGN KEY (target_user_id)
                REFERENCES dbo.Users(user_id)
        );
    END;

    IF EXISTS (
        SELECT 1
        WHERE COL_LENGTH(N'admin.UserUnlockOperations', N'operation_id') IS NULL
           OR COL_LENGTH(N'admin.UserUnlockOperations', N'administrator_user_id') IS NULL
           OR COL_LENGTH(N'admin.UserUnlockOperations', N'idempotency_key') IS NULL
           OR COL_LENGTH(N'admin.UserUnlockOperations', N'request_hash') IS NULL
           OR COL_LENGTH(N'admin.UserUnlockOperations', N'target_user_id') IS NULL
           OR COL_LENGTH(N'admin.UserUnlockOperations', N'restored_status') IS NULL
           OR COL_LENGTH(N'admin.UserUnlockOperations', N'unlocked_at_utc') IS NULL)
        THROW 51000, 'TM-95 cannot upgrade a partial admin.UserUnlockOperations table.', 1;

    IF EXISTS (
        SELECT 1
        FROM sys.columns AS columnDefinition
        WHERE columnDefinition.object_id = OBJECT_ID(N'admin.UserUnlockOperations')
          AND (
              (columnDefinition.name = N'operation_id'
                  AND (columnDefinition.system_type_id <> TYPE_ID(N'bigint')
                       OR columnDefinition.is_nullable <> 0 OR columnDefinition.is_identity <> 1))
              OR (columnDefinition.name IN (N'administrator_user_id', N'target_user_id')
                  AND (columnDefinition.system_type_id <> TYPE_ID(N'bigint')
                       OR columnDefinition.is_nullable <> 0))
              OR (columnDefinition.name IN (N'idempotency_key', N'request_hash')
                  AND (columnDefinition.system_type_id <> TYPE_ID(N'varchar')
                       OR columnDefinition.max_length <> 128 OR columnDefinition.is_nullable <> 0))
              OR (columnDefinition.name = N'restored_status'
                  AND (columnDefinition.system_type_id <> TYPE_ID(N'varchar')
                       OR columnDefinition.max_length <> 24 OR columnDefinition.is_nullable <> 0))
              OR (columnDefinition.name = N'unlocked_at_utc'
                  AND (columnDefinition.system_type_id <> TYPE_ID(N'datetime2')
                       OR columnDefinition.is_nullable <> 0))
          ))
        THROW 51000, 'TM-95 UserUnlockOperations column contract mismatch.', 1;

    IF OBJECT_ID(N'admin.CK_UserUnlockOperations_RestoredStatus', N'C') IS NOT NULL
        ALTER TABLE admin.UserUnlockOperations DROP CONSTRAINT CK_UserUnlockOperations_RestoredStatus;
    ALTER TABLE admin.UserUnlockOperations ADD CONSTRAINT CK_UserUnlockOperations_RestoredStatus CHECK (
        restored_status IN ('PendingEmailVerification','Active','PendingApproval','Rejected','Inactive')
    );

    IF OBJECT_ID(N'admin.FK_UserUnlockOperations_Administrator', N'F') IS NOT NULL
        ALTER TABLE admin.UserUnlockOperations DROP CONSTRAINT FK_UserUnlockOperations_Administrator;
    ALTER TABLE admin.UserUnlockOperations ADD CONSTRAINT FK_UserUnlockOperations_Administrator
        FOREIGN KEY (administrator_user_id) REFERENCES dbo.Users(user_id);

    IF OBJECT_ID(N'admin.FK_UserUnlockOperations_Target', N'F') IS NOT NULL
        ALTER TABLE admin.UserUnlockOperations DROP CONSTRAINT FK_UserUnlockOperations_Target;
    ALTER TABLE admin.UserUnlockOperations ADD CONSTRAINT FK_UserUnlockOperations_Target
        FOREIGN KEY (target_user_id) REFERENCES dbo.Users(user_id);

    IF EXISTS (
        SELECT 1 FROM sys.indexes
        WHERE object_id = OBJECT_ID(N'admin.UserUnlockOperations')
          AND name = N'UX_UserUnlockOperations_AdministratorKey')
        DROP INDEX UX_UserUnlockOperations_AdministratorKey ON admin.UserUnlockOperations;
    CREATE UNIQUE INDEX UX_UserUnlockOperations_AdministratorKey
        ON admin.UserUnlockOperations(administrator_user_id, idempotency_key);

    COMMIT TRANSACTION;
END TRY
BEGIN CATCH
    IF XACT_STATE() <> 0 ROLLBACK TRANSACTION;
    THROW;
END CATCH;
GO
