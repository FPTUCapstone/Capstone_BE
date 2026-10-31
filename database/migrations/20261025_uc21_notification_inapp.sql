/* UC-21: allow recipient-only in-app notification records. Deploy before BE release. */
SET XACT_ABORT ON;
BEGIN TRANSACTION;

IF OBJECT_ID(N'dbo.Notifications', N'U') IS NULL
    THROW 51023, 'UC-21 InApp migration requires dbo.Notifications.', 1;

IF EXISTS (
    SELECT 1 FROM sys.check_constraints
    WHERE parent_object_id = OBJECT_ID(N'dbo.Notifications')
      AND name = N'CK_Notifications_Channel'
      AND definition NOT LIKE N'%InApp%')
    THROW 51026, 'UC-21 found an unexpected named channel constraint; review manually.', 1;

IF NOT EXISTS (
    SELECT 1 FROM sys.check_constraints
    WHERE parent_object_id = OBJECT_ID(N'dbo.Notifications')
      AND name = N'CK_Notifications_Channel')
BEGIN
    DECLARE @legacyName sysname;
    DECLARE @legacyDefinition nvarchar(max);
    DECLARE @legacyCount int;
    DECLARE @channelColumnId int = COLUMNPROPERTY(OBJECT_ID(N'dbo.Notifications'), N'channel', 'ColumnId');

    SELECT @legacyCount = COUNT(*)
    FROM sys.check_constraints
    WHERE parent_object_id = OBJECT_ID(N'dbo.Notifications')
      AND (parent_column_id = @channelColumnId
           OR (parent_column_id = 0 AND CHARINDEX(N'channel', definition) > 0));

    IF @legacyCount > 1
        THROW 51024, 'UC-21 found multiple notification channel constraints; review manually.', 1;

    SELECT @legacyName = name, @legacyDefinition = definition
    FROM sys.check_constraints
    WHERE parent_object_id = OBJECT_ID(N'dbo.Notifications')
      AND (parent_column_id = @channelColumnId
           OR (parent_column_id = 0 AND CHARINDEX(N'channel', definition) > 0));

    IF @legacyName IS NOT NULL
    BEGIN
        IF @legacyDefinition NOT LIKE N'%Push%'
            OR @legacyDefinition NOT LIKE N'%Email%'
            OR @legacyDefinition NOT LIKE N'%SMS%'
            OR @legacyDefinition LIKE N'%InApp%'
            THROW 51025, 'UC-21 found an unexpected channel constraint; review manually.', 1;

        DECLARE @dropLegacyChannelConstraint nvarchar(max) =
            N'ALTER TABLE dbo.Notifications DROP CONSTRAINT ' + QUOTENAME(@legacyName);
        EXEC sys.sp_executesql @dropLegacyChannelConstraint;
    END;

    ALTER TABLE dbo.Notifications WITH CHECK
        ADD CONSTRAINT CK_Notifications_Channel
        CHECK (channel IN ('Push', 'Email', 'SMS', 'InApp'));
END;

COMMIT TRANSACTION;
