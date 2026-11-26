-- Approved direct 1:1 Messenger MVP. No runtime data or seed rows.
SET XACT_ABORT ON;
BEGIN TRANSACTION;
DECLARE @lockResult int;
EXEC @lockResult = sys.sp_getapplock @Resource = N'TripMate.DirectChat.Schema',
    @LockMode = N'Exclusive', @LockOwner = N'Transaction', @LockTimeout = 30000;
IF @lockResult < 0 THROW 51000, 'Cannot acquire direct chat schema lock.', 1;
IF SCHEMA_ID(N'social') IS NULL EXEC(N'CREATE SCHEMA social');

IF OBJECT_ID(N'social.DirectConversations', N'U') IS NULL
BEGIN
    CREATE TABLE social.DirectConversations (
        conversation_id bigint IDENTITY(1,1) NOT NULL CONSTRAINT PK_DirectConversations PRIMARY KEY,
        lower_user_id bigint NOT NULL,
        upper_user_id bigint NOT NULL,
        created_at datetime2(7) NOT NULL,
        lower_read_through_message_id bigint NOT NULL CONSTRAINT DF_DirectConversations_LowerRead DEFAULT (0),
        upper_read_through_message_id bigint NOT NULL CONSTRAINT DF_DirectConversations_UpperRead DEFAULT (0),
        CONSTRAINT CK_DirectConversations_OrderedPair CHECK (lower_user_id < upper_user_id),
        CONSTRAINT CK_DirectConversations_ReadCursors CHECK (lower_read_through_message_id >= 0 AND upper_read_through_message_id >= 0),
        CONSTRAINT FK_DirectConversations_LowerUser FOREIGN KEY (lower_user_id) REFERENCES dbo.Users(user_id),
        CONSTRAINT FK_DirectConversations_UpperUser FOREIGN KEY (upper_user_id) REFERENCES dbo.Users(user_id)
    );
END;
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'social.DirectConversations') AND name = N'UX_DirectConversations_Pair')
    CREATE UNIQUE INDEX UX_DirectConversations_Pair ON social.DirectConversations(lower_user_id, upper_user_id);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'social.DirectConversations') AND name = N'IX_DirectConversations_Upper')
    CREATE INDEX IX_DirectConversations_Upper ON social.DirectConversations(upper_user_id, conversation_id);

IF OBJECT_ID(N'social.DirectMessages', N'U') IS NULL
BEGIN
    CREATE TABLE social.DirectMessages (
        message_id bigint IDENTITY(1,1) NOT NULL CONSTRAINT PK_DirectMessages PRIMARY KEY,
        conversation_id bigint NOT NULL,
        sender_user_id bigint NOT NULL,
        content nvarchar(4000) NOT NULL,
        client_message_id uniqueidentifier NOT NULL,
        created_at datetime2(7) NOT NULL,
        CONSTRAINT CK_DirectMessages_Content CHECK (LEN(LTRIM(RTRIM(content))) >= 1),
        CONSTRAINT CK_DirectMessages_ClientKey CHECK (client_message_id <> '00000000-0000-0000-0000-000000000000'),
        CONSTRAINT FK_DirectMessages_Conversation FOREIGN KEY (conversation_id) REFERENCES social.DirectConversations(conversation_id) ON DELETE CASCADE,
        CONSTRAINT FK_DirectMessages_Sender FOREIGN KEY (sender_user_id) REFERENCES dbo.Users(user_id)
    );
END;
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'social.DirectMessages') AND name = N'UX_DirectMessages_ClientKey')
    CREATE UNIQUE INDEX UX_DirectMessages_ClientKey ON social.DirectMessages(conversation_id, sender_user_id, client_message_id);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'social.DirectMessages') AND name = N'IX_DirectMessages_History')
    CREATE INDEX IX_DirectMessages_History ON social.DirectMessages(conversation_id, message_id);
COMMIT TRANSACTION;
GO
