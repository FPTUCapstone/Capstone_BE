-- Creator edits with optimistic interaction safety; additive to travel chat.
SET QUOTED_IDENTIFIER ON;
SET XACT_ABORT ON;
BEGIN TRANSACTION;
DECLARE @result int;
EXEC @result=sys.sp_getapplock @Resource=N'TripMate.GroupCards.Schema',@LockMode=N'Exclusive',@LockOwner=N'Transaction',@LockTimeout=30000;
IF @result<0 THROW 51000,'Unable to lock group card schema.',1;
IF OBJECT_ID(N'social.GroupMessages',N'U') IS NULL THROW 51001,'Travel group chat must be installed first.',1;
IF COL_LENGTH(N'social.GroupMessages',N'revision') IS NULL EXEC(N'ALTER TABLE social.GroupMessages ADD revision bigint NOT NULL CONSTRAINT DF_GroupMessages_Revision DEFAULT(0)');
IF COL_LENGTH(N'social.GroupMessages',N'interaction_revision') IS NULL EXEC(N'ALTER TABLE social.GroupMessages ADD interaction_revision bigint NOT NULL CONSTRAINT DF_GroupMessages_InteractionRevision DEFAULT(0)');
IF COL_LENGTH(N'social.GroupMessages',N'edited_at') IS NULL EXEC(N'ALTER TABLE social.GroupMessages ADD edited_at datetime2(7) NULL');
IF COL_LENGTH(N'social.GroupMessages',N'financially_confirmed') IS NULL EXEC(N'ALTER TABLE social.GroupMessages ADD financially_confirmed bit NOT NULL CONSTRAINT DF_GroupMessages_FinanciallyConfirmed DEFAULT(0)');
IF COL_LENGTH(N'social.GroupMessages',N'original_content') IS NULL EXEC(N'ALTER TABLE social.GroupMessages ADD original_content nvarchar(max) NULL');
IF COL_LENGTH(N'social.GroupMessages',N'original_metadata_json') IS NULL EXEC(N'ALTER TABLE social.GroupMessages ADD original_metadata_json nvarchar(max) NULL');
-- Backfill original payload only once. Old paid records are conservatively immutable.
EXEC(N'UPDATE m SET original_content=m.content,original_metadata_json=m.metadata_json,
 financially_confirmed=CASE WHEN EXISTS(SELECT 1 FROM social.GroupSplitBillPayments p WHERE p.message_id=m.message_id AND p.is_paid=1) THEN 1 ELSE m.financially_confirmed END
 FROM social.GroupMessages m WHERE original_content IS NULL');
IF OBJECT_ID(N'social.GroupMessageEditOperations',N'U') IS NULL
BEGIN
 CREATE TABLE social.GroupMessageEditOperations(
  operation_id bigint IDENTITY(1,1) NOT NULL CONSTRAINT PK_GroupMessageEditOperations PRIMARY KEY,
  message_id bigint NOT NULL,
  actor_id bigint NOT NULL,
  client_request_id uniqueidentifier NOT NULL,
  expected_revision bigint NOT NULL,
  content nvarchar(4000) NOT NULL,
  metadata_json nvarchar(max) NULL,
  CONSTRAINT FK_GroupMessageEditOperations_Message FOREIGN KEY(message_id) REFERENCES social.GroupMessages(message_id) ON DELETE CASCADE,
  CONSTRAINT FK_GroupMessageEditOperations_Actor FOREIGN KEY(actor_id) REFERENCES dbo.Users(user_id),
  CONSTRAINT CK_GroupMessageEditOperations_Revision CHECK(expected_revision>=0),
  CONSTRAINT CK_GroupMessageEditOperations_ClientKey CHECK(client_request_id<>'00000000-0000-0000-0000-000000000000')
 );
END;
IF NOT EXISTS(SELECT 1 FROM sys.indexes WHERE object_id=OBJECT_ID(N'social.GroupMessageEditOperations') AND name=N'UX_GroupMessageEditOperations_ClientKey')
 CREATE UNIQUE INDEX UX_GroupMessageEditOperations_ClientKey ON social.GroupMessageEditOperations(message_id,actor_id,client_request_id);
IF NOT EXISTS(SELECT 1 FROM sys.indexes WHERE object_id=OBJECT_ID(N'social.GroupMessages') AND name=N'IX_GroupMessages_Important')
 CREATE INDEX IX_GroupMessages_Important ON social.GroupMessages(group_id,message_type,message_id);
IF EXISTS(SELECT 1 FROM (VALUES (N'revision',N'bigint',8,0),(N'interaction_revision',N'bigint',8,0),(N'edited_at',N'datetime2',8,1),(N'financially_confirmed',N'bit',1,0),(N'original_content',N'nvarchar',-1,1),(N'original_metadata_json',N'nvarchar',-1,1)) expected(col,type_name,byte_length,nullable)
 LEFT JOIN sys.columns c ON c.object_id=OBJECT_ID(N'social.GroupMessages') AND c.name=expected.col
 WHERE c.column_id IS NULL OR TYPE_NAME(c.user_type_id)<>expected.type_name OR c.max_length<>expected.byte_length OR c.is_nullable<>expected.nullable
 OR(c.name=N'edited_at' AND c.scale<>7)) THROW 51002,'Incompatible social.GroupMessages columns.',1;
IF EXISTS(SELECT 1 FROM (VALUES (N'operation_id',N'bigint',8,0),(N'message_id',N'bigint',8,0),(N'actor_id',N'bigint',8,0),(N'client_request_id',N'uniqueidentifier',16,0),(N'expected_revision',N'bigint',8,0),(N'content',N'nvarchar',8000,0),(N'metadata_json',N'nvarchar',-1,1)) expected(col,type_name,byte_length,nullable)
 LEFT JOIN sys.columns c ON c.object_id=OBJECT_ID(N'social.GroupMessageEditOperations') AND c.name=expected.col
 WHERE c.column_id IS NULL OR TYPE_NAME(c.user_type_id)<>expected.type_name OR c.max_length<>expected.byte_length OR c.is_nullable<>expected.nullable
 OR(c.name=N'edited_at' AND c.scale<>7)) THROW 51002,'Incompatible social.GroupMessageEditOperations columns.',1;
IF NOT EXISTS(SELECT 1 FROM sys.indexes i WHERE i.object_id=OBJECT_ID(N'social.GroupMessageEditOperations') AND i.name=N'UX_GroupMessageEditOperations_ClientKey'
 AND i.is_unique=1 AND i.is_disabled=0 AND i.has_filter=0
 AND(SELECT COUNT(*) FROM sys.index_columns ic WHERE ic.object_id=i.object_id AND ic.index_id=i.index_id AND ic.key_ordinal>0)=3
 AND EXISTS(SELECT 1 FROM sys.index_columns ic JOIN sys.columns c ON c.object_id=ic.object_id AND c.column_id=ic.column_id WHERE ic.object_id=i.object_id AND ic.index_id=i.index_id AND ic.key_ordinal=1 AND c.name=N'message_id')
 AND EXISTS(SELECT 1 FROM sys.index_columns ic JOIN sys.columns c ON c.object_id=ic.object_id AND c.column_id=ic.column_id WHERE ic.object_id=i.object_id AND ic.index_id=i.index_id AND ic.key_ordinal=2 AND c.name=N'actor_id')
 AND EXISTS(SELECT 1 FROM sys.index_columns ic JOIN sys.columns c ON c.object_id=ic.object_id AND c.column_id=ic.column_id WHERE ic.object_id=i.object_id AND ic.index_id=i.index_id AND ic.key_ordinal=3 AND c.name=N'client_request_id'))
 THROW 51003,'Incompatible edit idempotency index.',1;
COMMIT;
GO
