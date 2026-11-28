-- Independent chat leave. Apply after 20261127_add_chat_groups.sql, before deploying host transfers.
-- Creator and original selection are backfilled once, before any host or member mutation.
SET XACT_ABORT ON;
BEGIN TRANSACTION;
DECLARE @leaveSchemaLock int;
EXEC @leaveSchemaLock = sys.sp_getapplock @Resource = N'TripMate.ChatGroups.Schema',
    @LockMode = N'Exclusive', @LockOwner = N'Transaction', @LockTimeout = 30000;
IF @leaveSchemaLock < 0 THROW 51000, 'Cannot acquire chat group schema lock.', 1;
IF OBJECT_ID(N'social.ChatGroups', N'U') IS NULL
    THROW 51001, 'Apply the independent chat groups schema first.', 1;
IF COL_LENGTH(N'social.ChatGroups', N'created_by_user_id') IS NULL
    ALTER TABLE social.ChatGroups ADD created_by_user_id bigint NULL;
IF COL_LENGTH(N'social.ChatGroups', N'initial_member_user_ids') IS NULL
    ALTER TABLE social.ChatGroups ADD initial_member_user_ids nvarchar(1100) NULL;
EXEC(N'
    UPDATE g WITH (TABLOCKX, HOLDLOCK) SET created_by_user_id = host_user_id
    FROM social.ChatGroups g WHERE created_by_user_id IS NULL;
    UPDATE g SET initial_member_user_ids = members.ids
    FROM social.ChatGroups g
    CROSS APPLY (SELECT STRING_AGG(CONVERT(nvarchar(max), m.user_id), N'','')
        WITHIN GROUP (ORDER BY m.user_id) AS ids FROM social.ChatGroupMembers m WHERE m.group_id = g.group_id) members
    WHERE g.initial_member_user_ids IS NULL;
    IF EXISTS (SELECT 1 FROM social.ChatGroups WHERE created_by_user_id IS NULL OR initial_member_user_ids IS NULL)
        THROW 51001, ''Cannot backfill immutable chat group creation data.'', 1;
    ALTER TABLE social.ChatGroups ALTER COLUMN created_by_user_id bigint NOT NULL;
    ALTER TABLE social.ChatGroups ALTER COLUMN initial_member_user_ids nvarchar(1100) NOT NULL;
');
IF NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE parent_object_id=OBJECT_ID(N'social.ChatGroups') AND name=N'FK_ChatGroups_Creator')
    EXEC(N'ALTER TABLE social.ChatGroups ADD CONSTRAINT FK_ChatGroups_Creator FOREIGN KEY (created_by_user_id) REFERENCES dbo.Users(user_id)');
-- Accept only the known legacy/current key shape; fail rather than silently replacing unrelated DDL.
IF EXISTS (SELECT 1 FROM sys.indexes WHERE object_id=OBJECT_ID(N'social.ChatGroups') AND name=N'UX_ChatGroups_ClientKey')
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM sys.indexes i
        WHERE i.object_id=OBJECT_ID(N'social.ChatGroups') AND i.name=N'UX_ChatGroups_ClientKey'
            AND i.is_unique=1 AND i.has_filter=0 AND i.is_disabled=0
            AND (SELECT COUNT(*) FROM sys.index_columns ic WHERE ic.object_id=i.object_id AND ic.index_id=i.index_id AND ic.key_ordinal>0)=2
            AND EXISTS (SELECT 1 FROM sys.index_columns ic JOIN sys.columns c ON c.object_id=ic.object_id AND c.column_id=ic.column_id
                WHERE ic.object_id=i.object_id AND ic.index_id=i.index_id AND ic.key_ordinal=1 AND c.name IN (N'host_user_id',N'created_by_user_id'))
            AND EXISTS (SELECT 1 FROM sys.index_columns ic JOIN sys.columns c ON c.object_id=ic.object_id AND c.column_id=ic.column_id
                WHERE ic.object_id=i.object_id AND ic.index_id=i.index_id AND ic.key_ordinal=2 AND c.name=N'client_request_id'))
        THROW 51002, 'Existing chat creation index is incompatible.', 1;
    IF EXISTS (SELECT 1 FROM sys.indexes i JOIN sys.index_columns ic ON ic.object_id=i.object_id AND ic.index_id=i.index_id
        JOIN sys.columns c ON c.object_id=ic.object_id AND c.column_id=ic.column_id
        WHERE i.object_id=OBJECT_ID(N'social.ChatGroups') AND i.name=N'UX_ChatGroups_ClientKey'
            AND ic.key_ordinal=1 AND c.name=N'host_user_id')
        DROP INDEX UX_ChatGroups_ClientKey ON social.ChatGroups;
END;
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id=OBJECT_ID(N'social.ChatGroups') AND name=N'UX_ChatGroups_ClientKey')
    EXEC(N'CREATE UNIQUE INDEX UX_ChatGroups_ClientKey ON social.ChatGroups(created_by_user_id,client_request_id)');
IF OBJECT_ID(N'social.ChatGroupLeaveOperations', N'U') IS NULL
BEGIN
    CREATE TABLE social.ChatGroupLeaveOperations (
        operation_id bigint IDENTITY(1,1) NOT NULL CONSTRAINT PK_ChatGroupLeaveOperations PRIMARY KEY,
        group_id bigint NOT NULL,
        actor_id bigint NOT NULL,
        client_request_id uniqueidentifier NOT NULL,
        replacement_host_user_id bigint NULL,
        new_host_user_id bigint NOT NULL,
        left_at_utc datetime2(7) NOT NULL,
        CONSTRAINT CK_ChatGroupLeaveOperations_Key CHECK (client_request_id <> '00000000-0000-0000-0000-000000000000'),
        CONSTRAINT CK_ChatGroupLeaveOperations_Host CHECK (new_host_user_id <> actor_id
            AND (replacement_host_user_id IS NULL OR replacement_host_user_id = new_host_user_id)),
        CONSTRAINT FK_ChatGroupLeaveOperations_Group FOREIGN KEY (group_id) REFERENCES social.ChatGroups(group_id),
        CONSTRAINT FK_ChatGroupLeaveOperations_Actor FOREIGN KEY (actor_id) REFERENCES dbo.Users(user_id),
        CONSTRAINT FK_ChatGroupLeaveOperations_Host FOREIGN KEY (new_host_user_id) REFERENCES dbo.Users(user_id),
        CONSTRAINT FK_ChatGroupLeaveOperations_Replacement FOREIGN KEY (replacement_host_user_id) REFERENCES dbo.Users(user_id)
    );
END;
IF EXISTS (
    SELECT 1 FROM (VALUES
        (N'operation_id',N'bigint',8,0),(N'group_id',N'bigint',8,0),
        (N'actor_id',N'bigint',8,0),(N'client_request_id',N'uniqueidentifier',16,0),
        (N'replacement_host_user_id',N'bigint',8,1),(N'new_host_user_id',N'bigint',8,0),
        (N'left_at_utc',N'datetime2',8,0)) expected(column_name,type_name,byte_length,nullable)
    LEFT JOIN sys.columns c ON c.object_id=OBJECT_ID(N'social.ChatGroupLeaveOperations') AND c.name=expected.column_name
    WHERE c.column_id IS NULL OR TYPE_NAME(c.user_type_id)<>expected.type_name OR c.max_length<>expected.byte_length
        OR c.is_nullable<>expected.nullable OR (c.name=N'left_at_utc' AND c.scale<>7)
        OR (c.name=N'operation_id' AND c.is_identity<>1)
) THROW 51001, 'Existing chat leave receipt schema is incompatible.', 1;
IF EXISTS (SELECT 1 FROM sys.indexes WHERE object_id=OBJECT_ID(N'social.ChatGroupLeaveOperations') AND name=N'UX_ChatGroupLeaveOperations_ClientKey')
    AND NOT EXISTS (
        SELECT 1 FROM sys.indexes i WHERE i.object_id=OBJECT_ID(N'social.ChatGroupLeaveOperations') AND i.name=N'UX_ChatGroupLeaveOperations_ClientKey'
        AND i.is_unique=1 AND i.has_filter=0 AND i.is_disabled=0 AND i.is_hypothetical=0
        AND (SELECT COUNT(*) FROM sys.index_columns ic WHERE ic.object_id=i.object_id AND ic.index_id=i.index_id AND ic.key_ordinal>0)=3
        AND EXISTS (SELECT 1 FROM sys.index_columns ic JOIN sys.columns c ON c.object_id=ic.object_id AND c.column_id=ic.column_id
            WHERE ic.object_id=i.object_id AND ic.index_id=i.index_id AND ic.key_ordinal=1 AND c.name=N'group_id')
        AND EXISTS (SELECT 1 FROM sys.index_columns ic JOIN sys.columns c ON c.object_id=ic.object_id AND c.column_id=ic.column_id
            WHERE ic.object_id=i.object_id AND ic.index_id=i.index_id AND ic.key_ordinal=2 AND c.name=N'actor_id')
        AND EXISTS (SELECT 1 FROM sys.index_columns ic JOIN sys.columns c ON c.object_id=ic.object_id AND c.column_id=ic.column_id
            WHERE ic.object_id=i.object_id AND ic.index_id=i.index_id AND ic.key_ordinal=3 AND c.name=N'client_request_id'))
    THROW 51002, 'Existing chat leave receipt index is incompatible.', 1;
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id=OBJECT_ID(N'social.ChatGroupLeaveOperations') AND name=N'UX_ChatGroupLeaveOperations_ClientKey')
    CREATE UNIQUE INDEX UX_ChatGroupLeaveOperations_ClientKey ON social.ChatGroupLeaveOperations(group_id,actor_id,client_request_id);
COMMIT TRANSACTION;
GO
