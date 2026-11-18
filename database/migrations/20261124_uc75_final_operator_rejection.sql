/* UC-75: record the Tour Operator's final rejection after a Traveler counter-offer. */
SET XACT_ABORT ON;
SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
GO

IF OBJECT_ID(N'commerce.PostTripComplaintNegotiationEvents', N'U') IS NULL
    THROW 51000, 'UC-75 final rejection requires PostTripComplaintNegotiationEvents.', 1;
GO

BEGIN TRY
    BEGIN TRANSACTION;

    IF OBJECT_ID(N'commerce.CK_PostTripComplaintNegotiationEvents_Type', N'C') IS NOT NULL
        ALTER TABLE commerce.PostTripComplaintNegotiationEvents
            DROP CONSTRAINT CK_PostTripComplaintNegotiationEvents_Type;

    ALTER TABLE commerce.PostTripComplaintNegotiationEvents WITH CHECK ADD
        CONSTRAINT CK_PostTripComplaintNegotiationEvents_Type CHECK
        (event_type IN ('PartialRefundOffer', 'CounterOffer', 'Accepted', 'Declined', 'Rejected'));

    COMMIT TRANSACTION;
END TRY
BEGIN CATCH
    IF XACT_STATE() <> 0 ROLLBACK TRANSACTION;
    THROW;
END CATCH;
GO
