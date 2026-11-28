-- Independent chat groups. Travel group tables and contracts are unchanged.
SET XACT_ABORT ON;
BEGIN TRANSACTION;
DECLARE @lockResult int;
EXEC @lockResult = sys.sp_getapplock @Resource = N'TripMate.ChatGroups.Schema',
    @LockMode = N'Exclusive', @LockOwner = N'Transaction', @LockTimeout = 30000;
IF @lockResult < 0 THROW 51000, 'Cannot acquire chat group schema lock.', 1;
IF SCHEMA_ID(N'social') IS NULL EXEC(N'CREATE SCHEMA social');
IF OBJECT_ID(N'social.ChatGroups', N'U') IS NULL
BEGIN
    CREATE TABLE social.ChatGroups (
        group_id bigint IDENTITY(1,1) NOT NULL CONSTRAINT PK_ChatGroups PRIMARY KEY,
        group_name nvarchar(100) NOT NULL,
        host_user_id bigint NOT NULL,
        client_request_id uniqueidentifier NOT NULL,
        created_at datetime2(7) NOT NULL,
        CONSTRAINT CK_ChatGroups_Name CHECK (LEN(LTRIM(RTRIM(group_name))) BETWEEN 1 AND 100),
        CONSTRAINT CK_ChatGroups_ClientKey CHECK (client_request_id <> '00000000-0000-0000-0000-000000000000'),
        CONSTRAINT FK_ChatGroups_Host FOREIGN KEY (host_user_id) REFERENCES dbo.Users(user_id)
    );
END;
IF OBJECT_ID(N'social.ChatGroupMembers', N'U') IS NULL
BEGIN
    CREATE TABLE social.ChatGroupMembers (
        member_id bigint IDENTITY(1,1) NOT NULL CONSTRAINT PK_ChatGroupMembers PRIMARY KEY,
        group_id bigint NOT NULL,
        user_id bigint NOT NULL,
        read_through_message_id bigint NOT NULL,
        CONSTRAINT CK_ChatGroupMembers_Read CHECK (read_through_message_id >= 0),
        CONSTRAINT FK_ChatGroupMembers_Group FOREIGN KEY (group_id) REFERENCES social.ChatGroups(group_id) ON DELETE CASCADE,
        CONSTRAINT FK_ChatGroupMembers_User FOREIGN KEY (user_id) REFERENCES dbo.Users(user_id)
    );
END;
IF OBJECT_ID(N'social.ChatGroupMessages', N'U') IS NULL
BEGIN
    CREATE TABLE social.ChatGroupMessages (
        message_id bigint IDENTITY(1,1) NOT NULL CONSTRAINT PK_ChatGroupMessages PRIMARY KEY,
        group_id bigint NOT NULL,
        sender_user_id bigint NOT NULL,
        content nvarchar(4000) NOT NULL,
        client_message_id uniqueidentifier NOT NULL,
        created_at datetime2(7) NOT NULL,
        CONSTRAINT CK_ChatGroupMessages_Content CHECK (LEN(LTRIM(RTRIM(content))) BETWEEN 1 AND 4000),
        CONSTRAINT CK_ChatGroupMessages_ClientKey CHECK (client_message_id <> '00000000-0000-0000-0000-000000000000'),
        CONSTRAINT FK_ChatGroupMessages_Group FOREIGN KEY (group_id) REFERENCES social.ChatGroups(group_id) ON DELETE CASCADE,
        CONSTRAINT FK_ChatGroupMessages_Sender FOREIGN KEY (sender_user_id) REFERENCES dbo.Users(user_id)
    );
END;
IF EXISTS (
    SELECT 1 FROM (VALUES (N'group_id',N'bigint',8),
        (N'group_name',N'nvarchar',200),
        (N'host_user_id',N'bigint',8),
        (N'client_request_id',N'uniqueidentifier',16),
        (N'created_at',N'datetime2',8)) expected(column_name,type_name,byte_length)
    LEFT JOIN sys.columns c ON c.object_id = OBJECT_ID(N'social.ChatGroups') AND c.name = expected.column_name
    WHERE c.column_id IS NULL OR TYPE_NAME(c.user_type_id) <> expected.type_name
        OR c.max_length <> expected.byte_length OR c.is_nullable <> 0
        OR (c.name = N'created_at' AND c.scale <> 7)
        OR (c.name = N'group_id' AND c.is_identity <> 1)
) THROW 51001, 'Existing social.ChatGroups columns are incompatible.', 1;
IF EXISTS (
    SELECT 1 FROM (VALUES (N'member_id',N'bigint',8),
        (N'group_id',N'bigint',8),
        (N'user_id',N'bigint',8),
        (N'read_through_message_id',N'bigint',8)) expected(column_name,type_name,byte_length)
    LEFT JOIN sys.columns c ON c.object_id = OBJECT_ID(N'social.ChatGroupMembers') AND c.name = expected.column_name
    WHERE c.column_id IS NULL OR TYPE_NAME(c.user_type_id) <> expected.type_name
        OR c.max_length <> expected.byte_length OR c.is_nullable <> 0
        OR (c.name = N'created_at' AND c.scale <> 7)
        OR (c.name = N'member_id' AND c.is_identity <> 1)
) THROW 51001, 'Existing social.ChatGroupMembers columns are incompatible.', 1;
IF EXISTS (
    SELECT 1 FROM (VALUES (N'message_id',N'bigint',8),
        (N'group_id',N'bigint',8),
        (N'sender_user_id',N'bigint',8),
        (N'content',N'nvarchar',8000),
        (N'client_message_id',N'uniqueidentifier',16),
        (N'created_at',N'datetime2',8)) expected(column_name,type_name,byte_length)
    LEFT JOIN sys.columns c ON c.object_id = OBJECT_ID(N'social.ChatGroupMessages') AND c.name = expected.column_name
    WHERE c.column_id IS NULL OR TYPE_NAME(c.user_type_id) <> expected.type_name
        OR c.max_length <> expected.byte_length OR c.is_nullable <> 0
        OR (c.name = N'created_at' AND c.scale <> 7)
        OR (c.name = N'message_id' AND c.is_identity <> 1)
) THROW 51001, 'Existing social.ChatGroupMessages columns are incompatible.', 1;
IF EXISTS (SELECT 1 FROM sys.indexes WHERE object_id=OBJECT_ID(N'social.ChatGroups') AND name=N'UX_ChatGroups_ClientKey')
    AND NOT EXISTS (SELECT 1 FROM sys.indexes i WHERE i.object_id=OBJECT_ID(N'social.ChatGroups') AND i.name=N'UX_ChatGroups_ClientKey'
        AND i.is_unique=1 AND i.has_filter=0 AND i.is_disabled=0 AND i.is_hypothetical=0
        AND (SELECT COUNT(*) FROM sys.index_columns ic WHERE ic.object_id=i.object_id AND ic.index_id=i.index_id AND ic.key_ordinal>0)=2
        AND EXISTS (SELECT 1 FROM sys.index_columns ic JOIN sys.columns c ON c.object_id=ic.object_id AND c.column_id=ic.column_id WHERE ic.object_id=i.object_id AND ic.index_id=i.index_id AND ic.key_ordinal=1 AND c.name IN (N'host_user_id',N'created_by_user_id'))
        AND EXISTS (SELECT 1 FROM sys.index_columns ic JOIN sys.columns c ON c.object_id=ic.object_id AND c.column_id=ic.column_id WHERE ic.object_id=i.object_id AND ic.index_id=i.index_id AND ic.key_ordinal=2 AND c.name=N'client_request_id')) THROW 51002, 'Existing UX_ChatGroups_ClientKey is incompatible.', 1;
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id=OBJECT_ID(N'social.ChatGroups') AND name=N'UX_ChatGroups_ClientKey')
    CREATE UNIQUE INDEX UX_ChatGroups_ClientKey ON social.ChatGroups(host_user_id, client_request_id);
IF EXISTS (SELECT 1 FROM sys.indexes WHERE object_id=OBJECT_ID(N'social.ChatGroupMembers') AND name=N'UX_ChatGroupMembers_Membership')
    AND NOT EXISTS (SELECT 1 FROM sys.indexes i WHERE i.object_id=OBJECT_ID(N'social.ChatGroupMembers') AND i.name=N'UX_ChatGroupMembers_Membership'
        AND i.is_unique=1 AND i.has_filter=0 AND i.is_disabled=0 AND i.is_hypothetical=0
        AND (SELECT COUNT(*) FROM sys.index_columns ic WHERE ic.object_id=i.object_id AND ic.index_id=i.index_id AND ic.key_ordinal>0)=2
        AND EXISTS (SELECT 1 FROM sys.index_columns ic JOIN sys.columns c ON c.object_id=ic.object_id AND c.column_id=ic.column_id WHERE ic.object_id=i.object_id AND ic.index_id=i.index_id AND ic.key_ordinal=1 AND c.name=N'group_id')
        AND EXISTS (SELECT 1 FROM sys.index_columns ic JOIN sys.columns c ON c.object_id=ic.object_id AND c.column_id=ic.column_id WHERE ic.object_id=i.object_id AND ic.index_id=i.index_id AND ic.key_ordinal=2 AND c.name=N'user_id')) THROW 51002, 'Existing UX_ChatGroupMembers_Membership is incompatible.', 1;
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id=OBJECT_ID(N'social.ChatGroupMembers') AND name=N'UX_ChatGroupMembers_Membership')
    CREATE UNIQUE INDEX UX_ChatGroupMembers_Membership ON social.ChatGroupMembers(group_id, user_id);
IF EXISTS (SELECT 1 FROM sys.indexes WHERE object_id=OBJECT_ID(N'social.ChatGroupMembers') AND name=N'IX_ChatGroupMembers_User')
    AND NOT EXISTS (SELECT 1 FROM sys.indexes i WHERE i.object_id=OBJECT_ID(N'social.ChatGroupMembers') AND i.name=N'IX_ChatGroupMembers_User'
        AND i.is_unique=0 AND i.has_filter=0 AND i.is_disabled=0 AND i.is_hypothetical=0
        AND (SELECT COUNT(*) FROM sys.index_columns ic WHERE ic.object_id=i.object_id AND ic.index_id=i.index_id AND ic.key_ordinal>0)=2
        AND EXISTS (SELECT 1 FROM sys.index_columns ic JOIN sys.columns c ON c.object_id=ic.object_id AND c.column_id=ic.column_id WHERE ic.object_id=i.object_id AND ic.index_id=i.index_id AND ic.key_ordinal=1 AND c.name=N'user_id')
        AND EXISTS (SELECT 1 FROM sys.index_columns ic JOIN sys.columns c ON c.object_id=ic.object_id AND c.column_id=ic.column_id WHERE ic.object_id=i.object_id AND ic.index_id=i.index_id AND ic.key_ordinal=2 AND c.name=N'group_id')) THROW 51002, 'Existing IX_ChatGroupMembers_User is incompatible.', 1;
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id=OBJECT_ID(N'social.ChatGroupMembers') AND name=N'IX_ChatGroupMembers_User')
    CREATE INDEX IX_ChatGroupMembers_User ON social.ChatGroupMembers(user_id, group_id);
IF EXISTS (SELECT 1 FROM sys.indexes WHERE object_id=OBJECT_ID(N'social.ChatGroupMessages') AND name=N'UX_ChatGroupMessages_ClientKey')
    AND NOT EXISTS (SELECT 1 FROM sys.indexes i WHERE i.object_id=OBJECT_ID(N'social.ChatGroupMessages') AND i.name=N'UX_ChatGroupMessages_ClientKey'
        AND i.is_unique=1 AND i.has_filter=0 AND i.is_disabled=0 AND i.is_hypothetical=0
        AND (SELECT COUNT(*) FROM sys.index_columns ic WHERE ic.object_id=i.object_id AND ic.index_id=i.index_id AND ic.key_ordinal>0)=3
        AND EXISTS (SELECT 1 FROM sys.index_columns ic JOIN sys.columns c ON c.object_id=ic.object_id AND c.column_id=ic.column_id WHERE ic.object_id=i.object_id AND ic.index_id=i.index_id AND ic.key_ordinal=1 AND c.name=N'group_id')
        AND EXISTS (SELECT 1 FROM sys.index_columns ic JOIN sys.columns c ON c.object_id=ic.object_id AND c.column_id=ic.column_id WHERE ic.object_id=i.object_id AND ic.index_id=i.index_id AND ic.key_ordinal=2 AND c.name=N'sender_user_id')
        AND EXISTS (SELECT 1 FROM sys.index_columns ic JOIN sys.columns c ON c.object_id=ic.object_id AND c.column_id=ic.column_id WHERE ic.object_id=i.object_id AND ic.index_id=i.index_id AND ic.key_ordinal=3 AND c.name=N'client_message_id')) THROW 51002, 'Existing UX_ChatGroupMessages_ClientKey is incompatible.', 1;
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id=OBJECT_ID(N'social.ChatGroupMessages') AND name=N'UX_ChatGroupMessages_ClientKey')
    CREATE UNIQUE INDEX UX_ChatGroupMessages_ClientKey ON social.ChatGroupMessages(group_id, sender_user_id, client_message_id);
IF EXISTS (SELECT 1 FROM sys.indexes WHERE object_id=OBJECT_ID(N'social.ChatGroupMessages') AND name=N'IX_ChatGroupMessages_History')
    AND NOT EXISTS (SELECT 1 FROM sys.indexes i WHERE i.object_id=OBJECT_ID(N'social.ChatGroupMessages') AND i.name=N'IX_ChatGroupMessages_History'
        AND i.is_unique=0 AND i.has_filter=0 AND i.is_disabled=0 AND i.is_hypothetical=0
        AND (SELECT COUNT(*) FROM sys.index_columns ic WHERE ic.object_id=i.object_id AND ic.index_id=i.index_id AND ic.key_ordinal>0)=2
        AND EXISTS (SELECT 1 FROM sys.index_columns ic JOIN sys.columns c ON c.object_id=ic.object_id AND c.column_id=ic.column_id WHERE ic.object_id=i.object_id AND ic.index_id=i.index_id AND ic.key_ordinal=1 AND c.name=N'group_id')
        AND EXISTS (SELECT 1 FROM sys.index_columns ic JOIN sys.columns c ON c.object_id=ic.object_id AND c.column_id=ic.column_id WHERE ic.object_id=i.object_id AND ic.index_id=i.index_id AND ic.key_ordinal=2 AND c.name=N'message_id')) THROW 51002, 'Existing IX_ChatGroupMessages_History is incompatible.', 1;
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id=OBJECT_ID(N'social.ChatGroupMessages') AND name=N'IX_ChatGroupMessages_History')
    CREATE INDEX IX_ChatGroupMessages_History ON social.ChatGroupMessages(group_id, message_id);
COMMIT TRANSACTION;
GO
