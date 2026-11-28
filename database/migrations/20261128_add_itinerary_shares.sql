-- Immutable server snapshots. No new message type or cascade to mutable itinerary data.
SET XACT_ABORT ON;
BEGIN TRANSACTION;
DECLARE @lockResult int;
EXEC @lockResult = sys.sp_getapplock @Resource = N'TripMate.ItineraryShares.Schema',
    @LockMode = N'Exclusive', @LockOwner = N'Transaction', @LockTimeout = 30000;
IF @lockResult < 0 THROW 51000, 'Cannot acquire itinerary share schema lock.', 1;
IF SCHEMA_ID(N'social') IS NULL EXEC(N'CREATE SCHEMA social');
IF OBJECT_ID(N'social.ItineraryShares', N'U') IS NULL
BEGIN
    CREATE TABLE social.ItineraryShares (
        share_id uniqueidentifier NOT NULL CONSTRAINT PK_ItineraryShares PRIMARY KEY,
        actor_id bigint NOT NULL,
        client_request_id uniqueidentifier NOT NULL,
        itinerary_id bigint NOT NULL,
        conversation_kind varchar(12) NOT NULL,
        conversation_id bigint NOT NULL,
        message_id bigint NOT NULL,
        title nvarchar(200) NOT NULL,
        shared_at datetime2(7) NOT NULL,
        itinerary_version int NOT NULL,
        items_json nvarchar(max) NOT NULL,
        CONSTRAINT CK_ItineraryShares_Ids CHECK (actor_id > 0 AND itinerary_id > 0 AND conversation_id > 0 AND message_id > 0 AND itinerary_version > 0),
        CONSTRAINT CK_ItineraryShares_Kind CHECK (conversation_kind COLLATE Latin1_General_100_BIN2 IN ('travel_group','chat_group','direct')),
        CONSTRAINT CK_ItineraryShares_Guids CHECK (share_id <> '00000000-0000-0000-0000-000000000000' AND client_request_id <> '00000000-0000-0000-0000-000000000000'),
        CONSTRAINT CK_ItineraryShares_Title CHECK (LEN(LTRIM(RTRIM(title))) > 0),
        CONSTRAINT CK_ItineraryShares_Items CHECK (ISJSON(items_json) = 1 AND LEFT(LTRIM(items_json),1) = '[')
    );
END;
IF EXISTS (
    SELECT 1 FROM (VALUES
        (N'share_id',N'uniqueidentifier',16),(N'actor_id',N'bigint',8),
        (N'client_request_id',N'uniqueidentifier',16),(N'itinerary_id',N'bigint',8),
        (N'conversation_kind',N'varchar',12),(N'conversation_id',N'bigint',8),
        (N'message_id',N'bigint',8),(N'title',N'nvarchar',400),
        (N'shared_at',N'datetime2',8),(N'itinerary_version',N'int',4),(N'items_json',N'nvarchar',-1)
    ) expected(col,type_name,byte_length)
    LEFT JOIN sys.columns c ON c.object_id=OBJECT_ID(N'social.ItineraryShares') AND c.name=expected.col
    WHERE c.column_id IS NULL OR TYPE_NAME(c.user_type_id)<>expected.type_name
        OR c.max_length<>expected.byte_length OR c.is_nullable<>0 OR (c.name=N'shared_at' AND c.scale<>7)
) THROW 51002, 'Incompatible social.ItineraryShares columns.', 1;
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id=OBJECT_ID(N'social.ItineraryShares') AND name=N'UX_ItineraryShares_ActorClientRequest')
    CREATE UNIQUE INDEX UX_ItineraryShares_ActorClientRequest ON social.ItineraryShares(actor_id,client_request_id);
IF NOT EXISTS (
    SELECT 1 FROM sys.indexes i WHERE i.object_id=OBJECT_ID(N'social.ItineraryShares')
        AND i.name=N'UX_ItineraryShares_ActorClientRequest' AND i.is_unique=1 AND i.is_disabled=0 AND i.has_filter=0
        AND (SELECT COUNT(*) FROM sys.index_columns ic WHERE ic.object_id=i.object_id AND ic.index_id=i.index_id AND ic.key_ordinal>0)=2
        AND EXISTS (SELECT 1 FROM sys.index_columns ic JOIN sys.columns c ON c.object_id=ic.object_id AND c.column_id=ic.column_id
            WHERE ic.object_id=i.object_id AND ic.index_id=i.index_id AND ic.key_ordinal=1 AND c.name=N'actor_id')
        AND EXISTS (SELECT 1 FROM sys.index_columns ic JOIN sys.columns c ON c.object_id=ic.object_id AND c.column_id=ic.column_id
            WHERE ic.object_id=i.object_id AND ic.index_id=i.index_id AND ic.key_ordinal=2 AND c.name=N'client_request_id')
) THROW 51003, 'Incompatible itinerary share retry index.', 1;
EXEC(N'CREATE OR ALTER TRIGGER social.TR_ItineraryShares_Immutable ON social.ItineraryShares
INSTEAD OF UPDATE AS
BEGIN
    SET NOCOUNT ON;
    THROW 51004, ''Itinerary share snapshots cannot be updated.'', 1;
END;');
COMMIT;
GO
