-- Messenger friendship persistence. No seed data or chat access changes.
SET XACT_ABORT ON;
BEGIN TRANSACTION;
DECLARE @lockResult int;
EXEC @lockResult = sys.sp_getapplock @Resource = N'TripMate.Friendships.Schema',
    @LockMode = N'Exclusive', @LockOwner = N'Transaction', @LockTimeout = 30000;
IF @lockResult < 0 THROW 51000, 'Cannot acquire friendship schema lock.', 1;
IF SCHEMA_ID(N'social') IS NULL EXEC(N'CREATE SCHEMA social');

IF OBJECT_ID(N'social.Friendships', N'U') IS NULL
BEGIN
    CREATE TABLE social.Friendships (
        friendship_id bigint IDENTITY(1,1) NOT NULL CONSTRAINT PK_Friendships PRIMARY KEY,
        lower_user_id bigint NOT NULL,
        upper_user_id bigint NOT NULL,
        requested_by_user_id bigint NOT NULL,
        status nvarchar(16) NOT NULL,
        created_at datetime2(7) NOT NULL,
        CONSTRAINT CK_Friendships_OrderedPair CHECK (lower_user_id < upper_user_id),
        CONSTRAINT CK_Friendships_Requester CHECK (requested_by_user_id IN (lower_user_id, upper_user_id)),
        CONSTRAINT CK_Friendships_Status CHECK (status IN ('Pending', 'Accepted')),
        CONSTRAINT FK_Friendships_LowerUser FOREIGN KEY (lower_user_id) REFERENCES dbo.Users(user_id),
        CONSTRAINT FK_Friendships_UpperUser FOREIGN KEY (upper_user_id) REFERENCES dbo.Users(user_id)
    );
END;
-- Fail explicitly on incompatible historical objects rather than accepting names as proof of parity.
IF EXISTS (
    SELECT 1
    FROM (VALUES
        (N'friendship_id', N'bigint', 8), (N'lower_user_id', N'bigint', 8),
        (N'upper_user_id', N'bigint', 8), (N'requested_by_user_id', N'bigint', 8),
        (N'status', N'nvarchar', 32), (N'created_at', N'datetime2', 8)
    ) expected(column_name, type_name, byte_length)
    LEFT JOIN sys.columns c ON c.object_id = OBJECT_ID(N'social.Friendships') AND c.name = expected.column_name
    WHERE c.column_id IS NULL OR TYPE_NAME(c.user_type_id) <> expected.type_name
        OR c.max_length <> expected.byte_length OR c.is_nullable <> 0
        OR (c.name = N'created_at' AND c.scale <> 7)
        OR (c.name = N'friendship_id' AND c.is_identity <> 1)
)
    THROW 51001, 'Existing social.Friendships columns are incompatible.', 1;

IF EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'social.Friendships') AND name = N'UX_Friendships_Pair')
    AND NOT EXISTS (
        SELECT 1 FROM sys.indexes i
        WHERE i.object_id = OBJECT_ID(N'social.Friendships') AND i.name = N'UX_Friendships_Pair'
            AND i.is_unique = 1 AND i.has_filter = 0 AND i.is_disabled = 0 AND i.is_hypothetical = 0
            AND (SELECT COUNT(*) FROM sys.index_columns ic
                WHERE ic.object_id = i.object_id AND ic.index_id = i.index_id AND ic.key_ordinal > 0) = 2
            AND EXISTS (SELECT 1 FROM sys.index_columns ic JOIN sys.columns c
                ON c.object_id = ic.object_id AND c.column_id = ic.column_id
                WHERE ic.object_id = i.object_id AND ic.index_id = i.index_id AND ic.key_ordinal = 1 AND c.name = N'lower_user_id')
            AND EXISTS (SELECT 1 FROM sys.index_columns ic JOIN sys.columns c
                ON c.object_id = ic.object_id AND c.column_id = ic.column_id
                WHERE ic.object_id = i.object_id AND ic.index_id = i.index_id AND ic.key_ordinal = 2 AND c.name = N'upper_user_id')
    )
    THROW 51002, 'Existing UX_Friendships_Pair must be enabled, unique, unfiltered and keyed by lower_user_id, upper_user_id.', 1;

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'social.Friendships') AND name = N'UX_Friendships_Pair')
    CREATE UNIQUE INDEX UX_Friendships_Pair ON social.Friendships(lower_user_id, upper_user_id);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'social.Friendships') AND name = N'IX_Friendships_Upper')
    CREATE INDEX IX_Friendships_Upper ON social.Friendships(upper_user_id, lower_user_id);
COMMIT TRANSACTION;
GO
