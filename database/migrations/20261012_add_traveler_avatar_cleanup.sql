SET XACT_ABORT ON;
BEGIN TRANSACTION;

IF COL_LENGTH(N'dbo.Users', N'avatar_public_id') IS NULL
    ALTER TABLE dbo.Users ADD avatar_public_id NVARCHAR(500) NULL;

IF OBJECT_ID(N'dbo.TravelerAvatarCleanupOutbox', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.TravelerAvatarCleanupOutbox (
        cleanup_id BIGINT IDENTITY(1,1) NOT NULL CONSTRAINT PK_TravelerAvatarCleanupOutbox PRIMARY KEY,
        public_id NVARCHAR(500) NOT NULL,
        cleanup_status VARCHAR(16) NOT NULL CONSTRAINT DF_TravelerAvatarCleanupOutbox_Status DEFAULT 'Pending',
        not_before_at DATETIME2 NOT NULL,
        attempt_count INT NOT NULL CONSTRAINT DF_TravelerAvatarCleanupOutbox_AttemptCount DEFAULT 0,
        lease_token UNIQUEIDENTIFIER NULL,
        lease_expires_at DATETIME2 NULL,
        last_error_code NVARCHAR(100) NULL,
        created_at DATETIME2 NOT NULL CONSTRAINT DF_TravelerAvatarCleanupOutbox_CreatedAt DEFAULT SYSUTCDATETIME(),
        updated_at DATETIME2 NOT NULL CONSTRAINT DF_TravelerAvatarCleanupOutbox_UpdatedAt DEFAULT SYSUTCDATETIME(),
        CONSTRAINT UX_TravelerAvatarCleanupOutbox_PublicId UNIQUE (public_id),
        CONSTRAINT CK_TravelerAvatarCleanupOutbox_Status CHECK (cleanup_status IN ('Pending', 'Leased'))
    );
    CREATE INDEX IX_TravelerAvatarCleanupOutbox_Due
        ON dbo.TravelerAvatarCleanupOutbox(cleanup_status, not_before_at, lease_expires_at, cleanup_id);
END;

COMMIT TRANSACTION;