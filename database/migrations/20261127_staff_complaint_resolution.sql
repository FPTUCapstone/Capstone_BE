-- Additive Staff review; preserves existing complaint/refund/evidence records.
SET XACT_ABORT ON;
BEGIN TRANSACTION;
IF OBJECT_ID(N'commerce.PostTripComplaints', N'U') IS NULL
    THROW 51000, 'Apply UC68/UC75 complaint migrations first.', 1;
IF OBJECT_ID(N'commerce.CK_PostTripComplaints_Status', N'C') IS NOT NULL
    ALTER TABLE commerce.PostTripComplaints DROP CONSTRAINT CK_PostTripComplaints_Status;
ALTER TABLE commerce.PostTripComplaints WITH CHECK ADD CONSTRAINT CK_PostTripComplaints_Status CHECK
    (status IN ('AwaitingOperatorResponse','AwaitingTravelerResponse','AwaitingOperatorReconsideration',
        'Approved','Rejected','Declined','Expired','AwaitingStaffReview','AwaitingStaffEvidence'));
IF OBJECT_ID(N'commerce.ComplaintStaffReviewEvents', N'U') IS NULL
BEGIN
    CREATE TABLE commerce.ComplaintStaffReviewEvents (
        review_event_id bigint IDENTITY(1,1) NOT NULL CONSTRAINT PK_ComplaintStaffReviewEvents PRIMARY KEY,
        complaint_id bigint NOT NULL,
        actor_user_id bigint NULL,
        operation_id uniqueidentifier NOT NULL,
        payload_fingerprint varchar(64) NOT NULL,
        action varchar(32) NOT NULL,
        reason nvarchar(1000) NOT NULL,
        contract_basis nvarchar(1000) NOT NULL,
        amount decimal(12,2) NULL,
        target_party varchar(16) NULL,
        request_event_id bigint NULL,
        created_at datetime2(7) NOT NULL,
        due_at datetime2(7) NULL,
        CONSTRAINT FK_ComplaintStaffReviewEvents_Complaint FOREIGN KEY(complaint_id) REFERENCES commerce.PostTripComplaints(complaint_id),
        CONSTRAINT FK_ComplaintStaffReviewEvents_Actor FOREIGN KEY(actor_user_id) REFERENCES dbo.Users(user_id),
        CONSTRAINT FK_ComplaintStaffReviewEvents_Request FOREIGN KEY(request_event_id) REFERENCES commerce.ComplaintStaffReviewEvents(review_event_id),
        CONSTRAINT CK_ComplaintStaffReviewEvents_Action CHECK(action IN ('RequestEvidence','Reject','RefundFull','RefundPartial','EvidenceSubmitted','EvidenceExpired','ReconsiderationRequested')),
        CONSTRAINT CK_ComplaintStaffReviewEvents_Party CHECK(target_party IS NULL OR target_party IN ('Traveler','Operator')),
        CONSTRAINT CK_ComplaintStaffReviewEvents_Amount CHECK(amount IS NULL OR (amount > 0 AND amount = FLOOR(amount)))
    );
    CREATE UNIQUE INDEX UX_ComplaintStaffReviewEvents_Operation ON commerce.ComplaintStaffReviewEvents(actor_user_id,operation_id);
    CREATE INDEX IX_ComplaintStaffReviewEvents_Complaint ON commerce.ComplaintStaffReviewEvents(complaint_id,review_event_id);
END;
IF COL_LENGTH(N'commerce.ComplaintEvidence', N'staff_review_event_id') IS NULL
BEGIN
    ALTER TABLE commerce.ComplaintEvidence ADD staff_review_event_id bigint NULL;
    ALTER TABLE commerce.ComplaintEvidence ADD CONSTRAINT FK_ComplaintEvidence_StaffReview FOREIGN KEY(staff_review_event_id)
        REFERENCES commerce.ComplaintStaffReviewEvents(review_event_id);
END;
COMMIT TRANSACTION;
