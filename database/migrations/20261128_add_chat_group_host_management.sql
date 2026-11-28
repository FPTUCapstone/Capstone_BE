-- Independent chat host management. Apply after chat-group creation/leave migrations.
-- Backfill creation names before enabling rename; retain all memberships and messages.
SET XACT_ABORT ON;
BEGIN TRANSACTION;
DECLARE @managementSchemaLock int;
EXEC @managementSchemaLock = sys.sp_getapplock @Resource = N'TripMate.ChatGroups.Schema',
    @LockMode = N'Exclusive', @LockOwner = N'Transaction', @LockTimeout = 30000;
IF @managementSchemaLock < 0 THROW 51000, 'Cannot acquire chat group management schema lock.', 1;
IF OBJECT_ID(N'social.ChatGroups', N'U') IS NULL
    THROW 51001, 'Apply the chat group creation migration first.', 1;
IF COL_LENGTH(N'social.ChatGroups', N'initial_group_name') IS NULL
    ALTER TABLE social.ChatGroups ADD initial_group_name nvarchar(100) NULL;
IF COL_LENGTH(N'social.ChatGroups', N'is_closed') IS NULL
    ALTER TABLE social.ChatGroups ADD is_closed bit NOT NULL
        CONSTRAINT DF_ChatGroups_IsClosed DEFAULT (0) WITH VALUES;
IF EXISTS (
    SELECT 1 FROM (VALUES (N'initial_group_name',N'nvarchar',200),
        (N'is_closed',N'bit',1)) expected(column_name,type_name,byte_length)
    LEFT JOIN sys.columns c ON c.object_id=OBJECT_ID(N'social.ChatGroups') AND c.name=expected.column_name
    WHERE c.column_id IS NULL OR TYPE_NAME(c.user_type_id)<>expected.type_name
        OR c.max_length<>expected.byte_length OR c.is_computed<>0
        OR (c.name=N'is_closed' AND c.is_nullable<>0)
) THROW 51001, 'Existing chat group management columns are incompatible.', 1;
-- Dynamic batch resolves columns added above even on the first application.
EXEC(N'
    UPDATE social.ChatGroups SET initial_group_name = group_name WHERE initial_group_name IS NULL;
    IF EXISTS (SELECT 1 FROM social.ChatGroups WHERE initial_group_name IS NULL
        OR LEN(LTRIM(RTRIM(initial_group_name))) NOT BETWEEN 1 AND 100)
        THROW 51001, ''Cannot backfill a valid original chat group name.'', 1;
    ALTER TABLE social.ChatGroups ALTER COLUMN initial_group_name nvarchar(100) NOT NULL;
');
IF NOT EXISTS (SELECT 1 FROM sys.check_constraints
    WHERE parent_object_id=OBJECT_ID(N'social.ChatGroups') AND name=N'CK_ChatGroups_InitialName')
    EXEC(N'ALTER TABLE social.ChatGroups WITH CHECK ADD CONSTRAINT CK_ChatGroups_InitialName
        CHECK (LEN(LTRIM(RTRIM(initial_group_name))) BETWEEN 1 AND 100)');
IF NOT EXISTS (SELECT 1 FROM sys.default_constraints d JOIN sys.columns c
    ON c.object_id=d.parent_object_id AND c.column_id=d.parent_column_id
    WHERE d.parent_object_id=OBJECT_ID(N'social.ChatGroups') AND c.name=N'is_closed')
    EXEC(N'ALTER TABLE social.ChatGroups ADD CONSTRAINT DF_ChatGroups_IsClosed DEFAULT (0) FOR is_closed');
IF EXISTS (SELECT 1 FROM sys.default_constraints d JOIN sys.columns c
    ON c.object_id=d.parent_object_id AND c.column_id=d.parent_column_id
    WHERE d.parent_object_id=OBJECT_ID(N'social.ChatGroups') AND c.name=N'is_closed'
        AND REPLACE(REPLACE(REPLACE(d.definition,N'(',N''),N')',N''),N' ',N'') <> N'0')
    THROW 51001, 'Existing chat group closed default is incompatible.', 1;
COMMIT TRANSACTION;
GO
