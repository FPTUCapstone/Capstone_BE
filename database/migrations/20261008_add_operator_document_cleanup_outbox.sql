-- UC-02: durable cleanup intent must commit before uploading sensitive documents.
SET XACT_ABORT ON;

BEGIN TRY
    BEGIN TRANSACTION;

    IF OBJECT_ID(N'dbo.OperatorDocuments', N'U') IS NULL
        THROW 51000, 'OperatorDocuments is required before adding cleanup outbox.', 1;

    IF OBJECT_ID(N'dbo.OperatorDocumentCleanupOutbox', N'U') IS NULL
    BEGIN
        CREATE TABLE dbo.OperatorDocumentCleanupOutbox (
            cleanup_id          BIGINT IDENTITY(1,1) PRIMARY KEY,
            public_id           NVARCHAR(500) NOT NULL,
            content_type        VARCHAR(32) NOT NULL
                CHECK (content_type IN ('application/pdf','image/jpeg','image/png')),
            expected_reference  NVARCHAR(500) NOT NULL,
            cleanup_status      VARCHAR(16) NOT NULL DEFAULT 'Pending'
                CHECK (cleanup_status IN ('Pending','Leased')),
            not_before_at       DATETIME2 NOT NULL,
            attempt_count       INT NOT NULL DEFAULT 0 CHECK (attempt_count >= 0),
            lease_token         UNIQUEIDENTIFIER NULL,
            lease_expires_at    DATETIME2 NULL,
            last_error_code     VARCHAR(100) NULL,
            created_at          DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(),
            updated_at          DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(),
            CONSTRAINT CK_OperatorDocumentCleanupOutbox_Lease CHECK (
                (cleanup_status = 'Pending' AND lease_token IS NULL AND lease_expires_at IS NULL) OR
                (cleanup_status = 'Leased' AND lease_token IS NOT NULL AND lease_expires_at IS NOT NULL))
        );
    END

    IF NOT EXISTS (SELECT 1 FROM sys.indexes
                   WHERE object_id = OBJECT_ID(N'dbo.OperatorDocumentCleanupOutbox')
                     AND name = N'UX_OperatorDocumentCleanupOutbox_PublicId')
        CREATE UNIQUE INDEX UX_OperatorDocumentCleanupOutbox_PublicId
            ON dbo.OperatorDocumentCleanupOutbox(public_id);

    IF NOT EXISTS (SELECT 1 FROM sys.indexes
                   WHERE object_id = OBJECT_ID(N'dbo.OperatorDocumentCleanupOutbox')
                     AND name = N'IX_OperatorDocumentCleanupOutbox_Due')
        CREATE INDEX IX_OperatorDocumentCleanupOutbox_Due
            ON dbo.OperatorDocumentCleanupOutbox(
                cleanup_status, not_before_at, lease_expires_at, cleanup_id);

    IF NOT EXISTS (SELECT 1 FROM sys.indexes
                   WHERE object_id = OBJECT_ID(N'dbo.OperatorDocuments')
                     AND name = N'IX_OperatorDocuments_FileUrl')
        CREATE INDEX IX_OperatorDocuments_FileUrl ON dbo.OperatorDocuments(file_url);

    COMMIT TRANSACTION;
END TRY
BEGIN CATCH
    IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;
    THROW;
END CATCH;
