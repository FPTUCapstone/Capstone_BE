-- Requires the existing travel group chat migration. No runtime data changes.
SET XACT_ABORT ON;
BEGIN TRANSACTION;
IF COL_LENGTH(N'social.GroupMessages', N'client_message_id') IS NULL
    ALTER TABLE social.GroupMessages ADD client_message_id uniqueidentifier NULL;
-- Dynamic SQL allows this script to add the column and index in the same batch.
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'social.GroupMessages')
    AND name = N'UX_GroupMessages_ClientMessageId')
    EXEC(N'CREATE UNIQUE INDEX UX_GroupMessages_ClientMessageId
        ON social.GroupMessages(group_id, sender_user_id, client_message_id)
        WHERE client_message_id IS NOT NULL');
IF COL_LENGTH(N'social.GroupMembers', N'last_read_chat_message_id') IS NULL
    ALTER TABLE social.GroupMembers ADD last_read_chat_message_id bigint NULL;
COMMIT TRANSACTION;
