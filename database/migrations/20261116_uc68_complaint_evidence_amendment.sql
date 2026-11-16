/* UC-68 product extension: one private evidence amendment per complaint. */
SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
GO

IF COL_LENGTH(N'commerce.PostTripComplaints', N'evidence_amendment_used_at') IS NULL
BEGIN
    ALTER TABLE commerce.PostTripComplaints
        ADD evidence_amendment_used_at DATETIME2 NULL;
END
GO

IF OBJECT_ID(N'commerce.PostTripComplaintEvidenceAmendmentOperations', N'U') IS NULL
BEGIN
    CREATE TABLE commerce.PostTripComplaintEvidenceAmendmentOperations (
        evidence_amendment_operation_id BIGINT IDENTITY(1,1) NOT NULL
            CONSTRAINT PK_PostTripComplaintEvidenceAmendmentOperations PRIMARY KEY,
        traveler_user_id BIGINT NOT NULL,
        idempotency_key UNIQUEIDENTIFIER NOT NULL,
        payload_fingerprint VARCHAR(64) NOT NULL,
        complaint_id BIGINT NOT NULL,
        created_at DATETIME2 NOT NULL,
        CONSTRAINT FK_PostTripComplaintEvidenceAmendmentOperations_Traveler
            FOREIGN KEY (traveler_user_id) REFERENCES dbo.Users(user_id),
        CONSTRAINT FK_PostTripComplaintEvidenceAmendmentOperations_Complaint
            FOREIGN KEY (complaint_id) REFERENCES commerce.PostTripComplaints(complaint_id)
    );
    CREATE UNIQUE INDEX UQ_PostTripComplaintEvidenceAmendmentOperations_TravelerKey
        ON commerce.PostTripComplaintEvidenceAmendmentOperations(traveler_user_id, idempotency_key);
END
GO
