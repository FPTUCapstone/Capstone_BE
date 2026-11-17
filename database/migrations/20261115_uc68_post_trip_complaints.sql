/* UC-68/69/75 shared post-trip complaint foundation. Idempotent SQL Server migration. */
SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
GO

IF SCHEMA_ID(N'commerce') IS NULL EXEC(N'CREATE SCHEMA commerce');
GO

IF OBJECT_ID(N'commerce.PostTripComplaints', N'U') IS NULL
BEGIN
    CREATE TABLE commerce.PostTripComplaints (
        complaint_id BIGINT IDENTITY(1,1) NOT NULL CONSTRAINT PK_PostTripComplaints PRIMARY KEY,
        booking_id BIGINT NOT NULL,
        traveler_user_id BIGINT NOT NULL,
        status VARCHAR(32) NOT NULL,
        reason_category VARCHAR(40) NOT NULL,
        reason_detail NVARCHAR(2000) NOT NULL,
        submitted_at DATETIME2 NOT NULL,
        response_deadline_at DATETIME2 NOT NULL,
        traveler_response_deadline_at DATETIME2 NULL,
        response_action VARCHAR(32) NULL,
        proposed_refund_amount DECIMAL(12,2) NULL,
        rejection_reason NVARCHAR(2000) NULL,
        responded_at DATETIME2 NULL,
        responded_by_operator_user_id BIGINT NULL,
        refundable_amount DECIMAL(12,2) NOT NULL,
        refund_id BIGINT NULL,
        row_version ROWVERSION NOT NULL,
        CONSTRAINT FK_PostTripComplaints_Booking FOREIGN KEY (booking_id) REFERENCES commerce.Bookings(booking_id),
        CONSTRAINT FK_PostTripComplaints_Traveler FOREIGN KEY (traveler_user_id) REFERENCES dbo.Users(user_id),
        CONSTRAINT FK_PostTripComplaints_RespondingOperator FOREIGN KEY (responded_by_operator_user_id) REFERENCES dbo.Users(user_id),
        CONSTRAINT CK_PostTripComplaints_Status CHECK (status IN ('AwaitingOperatorResponse','AwaitingTravelerResponse','Approved','Rejected','Declined','Expired')),
        CONSTRAINT CK_PostTripComplaints_Category CHECK (reason_category IN ('ItineraryNotAsAdvertised','ServiceQuality','GuideOrStaffConduct','SafetyIssue','ScheduleChangeOrDelay','Other')),
        CONSTRAINT CK_PostTripComplaints_RefundableAmount CHECK (refundable_amount >= 0),
        CONSTRAINT CK_PostTripComplaints_Deadline CHECK (response_deadline_at > submitted_at)
    );
    CREATE UNIQUE INDEX UX_PostTripComplaints_Booking ON commerce.PostTripComplaints(booking_id);
    CREATE INDEX IX_PostTripComplaints_Deadline ON commerce.PostTripComplaints(status, response_deadline_at);
END
GO

IF OBJECT_ID(N'commerce.PostTripComplaintSubmissionOperations', N'U') IS NULL
BEGIN
    CREATE TABLE commerce.PostTripComplaintSubmissionOperations (
        submission_operation_id BIGINT IDENTITY(1,1) NOT NULL CONSTRAINT PK_PostTripComplaintSubmissionOperations PRIMARY KEY,
        traveler_user_id BIGINT NOT NULL,
        idempotency_key UNIQUEIDENTIFIER NOT NULL,
        payload_fingerprint VARCHAR(64) NOT NULL,
        complaint_id BIGINT NOT NULL,
        created_at DATETIME2 NOT NULL,
        CONSTRAINT FK_PostTripComplaintSubmissionOperations_Traveler FOREIGN KEY (traveler_user_id) REFERENCES dbo.Users(user_id),
        CONSTRAINT FK_PostTripComplaintSubmissionOperations_Complaint FOREIGN KEY (complaint_id) REFERENCES commerce.PostTripComplaints(complaint_id)
    );
    CREATE UNIQUE INDEX UQ_PostTripComplaintSubmissionOperations_TravelerKey
        ON commerce.PostTripComplaintSubmissionOperations(traveler_user_id, idempotency_key);
END
GO

IF OBJECT_ID(N'commerce.ComplaintEvidence', N'U') IS NULL
BEGIN
    CREATE TABLE commerce.ComplaintEvidence (
        evidence_id BIGINT IDENTITY(1,1) NOT NULL CONSTRAINT PK_ComplaintEvidence PRIMARY KEY,
        complaint_id BIGINT NOT NULL,
        original_file_name NVARCHAR(255) NOT NULL,
        content_type VARCHAR(100) NOT NULL,
        byte_length BIGINT NOT NULL,
        storage_object_key VARCHAR(500) NOT NULL,
        checksum VARCHAR(128) NOT NULL,
        created_at DATETIME2 NOT NULL,
        CONSTRAINT FK_ComplaintEvidence_Complaint FOREIGN KEY (complaint_id) REFERENCES commerce.PostTripComplaints(complaint_id),
        CONSTRAINT CK_ComplaintEvidence_ByteLength CHECK (byte_length > 0)
    );
    CREATE INDEX IX_ComplaintEvidence_Complaint ON commerce.ComplaintEvidence(complaint_id);
    CREATE UNIQUE INDEX UX_ComplaintEvidence_StorageObjectKey ON commerce.ComplaintEvidence(storage_object_key);
END
GO
/* UC-68 owns complaint tables only; complaint-sourced refunds are added by TM-88. */
