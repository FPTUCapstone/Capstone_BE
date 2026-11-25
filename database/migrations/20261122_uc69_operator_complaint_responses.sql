/* UC-69: operator response idempotency and complaint-sourced refund linkage. */
SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
GO

IF OBJECT_ID(N'commerce.PostTripComplaints', N'U') IS NULL
    THROW 51000, 'UC-69 requires commerce.PostTripComplaints from UC-68.', 1;
IF OBJECT_ID(N'payment.Refunds', N'U') IS NULL
    THROW 51000, 'UC-69 requires payment.Refunds from UC-42.', 1;
GO

IF COL_LENGTH(N'payment.Refunds', N'source_type') IS NULL
BEGIN
    ALTER TABLE payment.Refunds ADD source_type VARCHAR(32) NULL;
    -- SQL Server binds column references before evaluating COL_LENGTH. Keep
    -- subsequent references in dynamic batches so first-time application is valid.
    EXEC(N'UPDATE payment.Refunds SET source_type = ''BookingCancellation'' WHERE source_type IS NULL;');
    EXEC(N'ALTER TABLE payment.Refunds ALTER COLUMN source_type VARCHAR(32) NOT NULL;');
END
GO

IF COL_LENGTH(N'payment.Refunds', N'complaint_id') IS NULL
    ALTER TABLE payment.Refunds ADD complaint_id BIGINT NULL;
GO

IF NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE parent_object_id = OBJECT_ID(N'payment.Refunds')
    AND name = N'CK_Refunds_SourceType')
    ALTER TABLE payment.Refunds ADD CONSTRAINT CK_Refunds_SourceType
        CHECK (source_type IN ('BookingCancellation', 'PostTripComplaint'));
GO

IF NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = N'FK_Refunds_Complaint')
    ALTER TABLE payment.Refunds ADD CONSTRAINT FK_Refunds_Complaint
        FOREIGN KEY (complaint_id) REFERENCES commerce.PostTripComplaints(complaint_id);
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'payment.Refunds')
    AND name = N'UX_Refunds_Complaint')
    CREATE UNIQUE INDEX UX_Refunds_Complaint ON payment.Refunds(complaint_id) WHERE complaint_id IS NOT NULL;
GO

IF NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = N'FK_PostTripComplaints_Refund')
    ALTER TABLE commerce.PostTripComplaints ADD CONSTRAINT FK_PostTripComplaints_Refund
        FOREIGN KEY (refund_id) REFERENCES payment.Refunds(refund_id);
GO

IF OBJECT_ID(N'commerce.PostTripComplaintResponseOperations', N'U') IS NULL
BEGIN
    CREATE TABLE commerce.PostTripComplaintResponseOperations (
        response_operation_id BIGINT IDENTITY(1,1) NOT NULL
            CONSTRAINT PK_PostTripComplaintResponseOperations PRIMARY KEY,
        operator_user_id BIGINT NOT NULL,
        idempotency_key UNIQUEIDENTIFIER NOT NULL,
        payload_fingerprint VARCHAR(64) NOT NULL,
        complaint_id BIGINT NOT NULL,
        created_at DATETIME2 NOT NULL,
        CONSTRAINT FK_PostTripComplaintResponseOperations_Operator
            FOREIGN KEY (operator_user_id) REFERENCES dbo.Users(user_id),
        CONSTRAINT FK_PostTripComplaintResponseOperations_Complaint
            FOREIGN KEY (complaint_id) REFERENCES commerce.PostTripComplaints(complaint_id)
    );
    CREATE UNIQUE INDEX UQ_PostTripComplaintResponseOperations_OperatorKey
        ON commerce.PostTripComplaintResponseOperations(operator_user_id, idempotency_key);
END
GO
