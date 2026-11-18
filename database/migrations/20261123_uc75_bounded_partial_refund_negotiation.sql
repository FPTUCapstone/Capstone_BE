/* UC-75: one Traveler counter-offer and one operator revision, with append-only history. */
SET XACT_ABORT ON;
SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
GO

IF OBJECT_ID(N'commerce.PostTripComplaints', N'U') IS NULL
    THROW 51000, 'UC-75 negotiation requires commerce.PostTripComplaints.', 1;
GO

BEGIN TRY
    BEGIN TRANSACTION;

    IF COL_LENGTH(N'commerce.PostTripComplaints', N'operator_reconsideration_deadline_at') IS NULL
        ALTER TABLE commerce.PostTripComplaints ADD operator_reconsideration_deadline_at DATETIME2 NULL;
    IF COL_LENGTH(N'commerce.PostTripComplaints', N'partial_refund_proposal_reason') IS NULL
        ALTER TABLE commerce.PostTripComplaints ADD partial_refund_proposal_reason NVARCHAR(1000) NULL;
    IF COL_LENGTH(N'commerce.PostTripComplaints', N'partial_refund_proposal_round') IS NULL
    BEGIN
        ALTER TABLE commerce.PostTripComplaints ADD partial_refund_proposal_round INT NOT NULL
            CONSTRAINT DF_PostTripComplaints_PartialRefundProposalRound DEFAULT (0) WITH VALUES;
        /* Dynamic SQL defers name binding until after ALTER TABLE has created the column. */
        EXEC sys.sp_executesql N'
            UPDATE commerce.PostTripComplaints
            SET partial_refund_proposal_round = 1
            WHERE response_action = ''ProposePartialRefund'';';
    END;

    IF OBJECT_ID(N'commerce.CK_PostTripComplaints_Status', N'C') IS NOT NULL
        ALTER TABLE commerce.PostTripComplaints DROP CONSTRAINT CK_PostTripComplaints_Status;
    ALTER TABLE commerce.PostTripComplaints WITH CHECK ADD CONSTRAINT CK_PostTripComplaints_Status CHECK
        (status IN ('AwaitingOperatorResponse','AwaitingTravelerResponse','AwaitingOperatorReconsideration',
                    'Approved','Rejected','Declined','Expired'));

    IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'commerce.PostTripComplaints')
        AND name = N'IX_PostTripComplaints_ReconsiderationDeadline')
        EXEC sys.sp_executesql N'
            CREATE INDEX IX_PostTripComplaints_ReconsiderationDeadline
                ON commerce.PostTripComplaints(status, operator_reconsideration_deadline_at)
                WHERE operator_reconsideration_deadline_at IS NOT NULL;';

    IF OBJECT_ID(N'commerce.PostTripComplaintNegotiationEvents', N'U') IS NULL
    BEGIN
        CREATE TABLE commerce.PostTripComplaintNegotiationEvents (
            negotiation_event_id BIGINT IDENTITY(1,1) NOT NULL
                CONSTRAINT PK_PostTripComplaintNegotiationEvents PRIMARY KEY,
            complaint_id BIGINT NOT NULL,
            round_number INT NOT NULL,
            actor_user_id BIGINT NOT NULL,
            actor VARCHAR(16) NOT NULL,
            event_type VARCHAR(24) NOT NULL,
            amount DECIMAL(12,2) NULL,
            reason NVARCHAR(1000) NULL,
            created_at DATETIME2 NOT NULL,
            CONSTRAINT CK_PostTripComplaintNegotiationEvents_Round CHECK (round_number IN (1, 2)),
            CONSTRAINT CK_PostTripComplaintNegotiationEvents_Actor CHECK (actor IN ('TourOperator', 'Traveler')),
            CONSTRAINT CK_PostTripComplaintNegotiationEvents_Type CHECK
                (event_type IN ('PartialRefundOffer', 'CounterOffer', 'Accepted', 'Declined')),
            CONSTRAINT CK_PostTripComplaintNegotiationEvents_Amount CHECK (amount IS NULL OR amount > 0),
            CONSTRAINT FK_PostTripComplaintNegotiationEvents_Complaint FOREIGN KEY (complaint_id)
                REFERENCES commerce.PostTripComplaints(complaint_id),
            CONSTRAINT FK_PostTripComplaintNegotiationEvents_Actor FOREIGN KEY (actor_user_id)
                REFERENCES dbo.Users(user_id)
        );
        CREATE INDEX IX_PostTripComplaintNegotiationEvents_Complaint
            ON commerce.PostTripComplaintNegotiationEvents(complaint_id, negotiation_event_id);
    END;

    COMMIT TRANSACTION;
END TRY
BEGIN CATCH
    IF XACT_STATE() <> 0 ROLLBACK TRANSACTION;
    THROW;
END CATCH;
GO
