-- Direct Refund Evidence Table (Phase 3 Task 13)
-- Adds: payment.DirectRefundEvidences, payment.DirectRefundTransferEvidences
-- Convention: idempotent; no credentials; legacy data intact.
SET XACT_ABORT ON;
SET NOCOUNT ON;

BEGIN TRY
    BEGIN TRANSACTION;

    IF SCHEMA_ID(N'payment') IS NULL
        THROW 51005, N'Direct refund evidence requires payment schema.', 1;

    -- Evidence files uploaded by TO for a DirectRefundCase (private; 1–3 per transfer report)
    IF OBJECT_ID(N'payment.DirectRefundEvidences', N'U') IS NULL
    BEGIN
        CREATE TABLE payment.DirectRefundEvidences (
            evidence_id         BIGINT         IDENTITY(1,1) NOT NULL
                                               CONSTRAINT PK_DirectRefundEvidences PRIMARY KEY,
            case_id             BIGINT         NOT NULL,
            original_file_name  NVARCHAR(255)  NOT NULL,
            content_type        NVARCHAR(100)  NOT NULL,
            byte_length         BIGINT         NOT NULL,
            storage_object_key  NVARCHAR(500)  NOT NULL,
            checksum            NVARCHAR(128)  NOT NULL,
            uploaded_at         DATETIME2      NOT NULL
                                               CONSTRAINT DF_DirectRefundEvidences_UploadedAt DEFAULT SYSUTCDATETIME(),
            CONSTRAINT FK_DirectRefundEvidences_Case
                FOREIGN KEY (case_id) REFERENCES payment.DirectRefundCases(case_id)
        );
        CREATE INDEX IX_DirectRefundEvidences_Case
            ON payment.DirectRefundEvidences(case_id);
    END;

    -- Many-to-many join: which evidence files are cited in each transfer report
    IF OBJECT_ID(N'payment.DirectRefundTransferEvidences', N'U') IS NULL
    BEGIN
        CREATE TABLE payment.DirectRefundTransferEvidences (
            transfer_id         BIGINT         NOT NULL,
            evidence_id         BIGINT         NOT NULL,
            CONSTRAINT PK_DirectRefundTransferEvidences
                PRIMARY KEY (transfer_id, evidence_id),
            CONSTRAINT FK_DRTransferEvidences_Transfer
                FOREIGN KEY (transfer_id) REFERENCES payment.DirectRefundTransfers(transfer_id),
            CONSTRAINT FK_DRTransferEvidences_Evidence
                FOREIGN KEY (evidence_id) REFERENCES payment.DirectRefundEvidences(evidence_id)
        );
    END;

    COMMIT TRANSACTION;
END TRY
BEGIN CATCH
    IF @@TRANCOUNT > 0
        ROLLBACK TRANSACTION;
    THROW;
END CATCH;
