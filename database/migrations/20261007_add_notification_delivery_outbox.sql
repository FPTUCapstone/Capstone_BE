/* TM-95 / UC-49: durable delivery lease for account-unlock email notifications. */
SET XACT_ABORT ON;
GO

BEGIN TRY
    BEGIN TRANSACTION;

    IF OBJECT_ID(N'dbo.Notifications', N'U') IS NULL
        THROW 51000, 'TM-95 notification delivery migration requires dbo.Notifications.', 1;

    IF COL_LENGTH(N'dbo.Notifications', N'delivery_attempt_count') IS NULL
        ALTER TABLE dbo.Notifications ADD delivery_attempt_count INT NOT NULL
            CONSTRAINT DF_Notifications_DeliveryAttemptCount DEFAULT 0;
    IF COL_LENGTH(N'dbo.Notifications', N'delivery_lease_token') IS NULL
        ALTER TABLE dbo.Notifications ADD delivery_lease_token UNIQUEIDENTIFIER NULL;
    IF COL_LENGTH(N'dbo.Notifications', N'delivery_lease_expires_at') IS NULL
        ALTER TABLE dbo.Notifications ADD delivery_lease_expires_at DATETIME2 NULL;
    IF COL_LENGTH(N'dbo.Notifications', N'next_delivery_attempt_at') IS NULL
        ALTER TABLE dbo.Notifications ADD next_delivery_attempt_at DATETIME2 NULL;
    IF COL_LENGTH(N'dbo.Notifications', N'delivery_last_error_code') IS NULL
        ALTER TABLE dbo.Notifications ADD delivery_last_error_code VARCHAR(100) NULL;

    IF EXISTS (
        SELECT 1
        FROM sys.columns AS column_definition
        WHERE column_definition.object_id = OBJECT_ID(N'dbo.Notifications')
          AND (
              (column_definition.name = N'delivery_attempt_count'
                AND (column_definition.system_type_id <> TYPE_ID(N'int') OR column_definition.is_nullable <> 0))
              OR (column_definition.name = N'delivery_lease_token'
                AND (column_definition.system_type_id <> TYPE_ID(N'uniqueidentifier') OR column_definition.is_nullable <> 1))
              OR (column_definition.name IN (N'delivery_lease_expires_at', N'next_delivery_attempt_at')
                AND (column_definition.system_type_id <> TYPE_ID(N'datetime2') OR column_definition.is_nullable <> 1))
              OR (column_definition.name = N'delivery_last_error_code'
                AND (column_definition.system_type_id <> TYPE_ID(N'varchar')
                     OR column_definition.max_length <> 100 OR column_definition.is_nullable <> 1))
          ))
        THROW 51000, 'TM-95 notification delivery column contract mismatch.', 1;

    IF OBJECT_ID(N'dbo.CK_Notifications_DeliveryAttemptCount', N'C') IS NOT NULL
        ALTER TABLE dbo.Notifications DROP CONSTRAINT CK_Notifications_DeliveryAttemptCount;
    EXEC sys.sp_executesql N'
        ALTER TABLE dbo.Notifications ADD CONSTRAINT CK_Notifications_DeliveryAttemptCount
            CHECK (delivery_attempt_count >= 0);';

    IF EXISTS (
        SELECT 1 FROM sys.indexes
        WHERE object_id = OBJECT_ID(N'dbo.Notifications')
          AND name = N'IX_Notifications_Delivery_Due')
        DROP INDEX IX_Notifications_Delivery_Due ON dbo.Notifications;
    EXEC sys.sp_executesql N'
        CREATE INDEX IX_Notifications_Delivery_Due ON dbo.Notifications(
            status, channel, type, next_delivery_attempt_at, delivery_lease_expires_at, notification_id);';

    COMMIT TRANSACTION;
END TRY
BEGIN CATCH
    IF XACT_STATE() <> 0 ROLLBACK TRANSACTION;
    THROW;
END CATCH;
GO
