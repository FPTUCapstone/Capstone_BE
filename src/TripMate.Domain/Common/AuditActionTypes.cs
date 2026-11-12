namespace TripMate.Domain.Common;

public static class AuditActionTypes
{
    public const string PoiCreate = "POI_CREATE";
    public const string PoiDeactivated = "POI_DEACTIVATED";

    public const string OperatorApplicationApprove = "ApproveOperatorApplication";

    public const string OperatorApplicationReject = "RejectOperatorApplication";

    public const string OperatorApplicationResubmit = "ResubmitOperatorApplication";

    public const string TourMediaUpload = "TOUR_MEDIA_UPLOAD";
    public const string TourMediaMetadataUpdated = "TOUR_MEDIA_METADATA_UPDATED";
    public const string TourMediaReordered = "TOUR_MEDIA_REORDERED";
    public const string TourMediaDeleted = "TOUR_MEDIA_DELETED";
    public const string TourSubmitForReview = "TOUR_SUBMIT_FOR_REVIEW";
    public const string TourDraftUpdated = "TOUR_DRAFT_UPDATED";
    public const string TourDeleted = "TOUR_DELETED";
    public const string TourApproved = "TOUR_APPROVED";
    public const string TourRejected = "TOUR_REJECTED";
    public const string TourPublished = "TOUR_PUBLISHED";
    public const string TourUnpublished = "TOUR_UNPUBLISHED";
    public const string TourWeatherReadinessReviewed = "TOUR_WEATHER_READINESS_REVIEWED";
    public const string TourEmergencyCancelled = "TOUR_EMERGENCY_CANCELLED";
    public const string TourEmergencyStopped = "TOUR_EMERGENCY_STOPPED";
    public const string TourEmergencyEvidenceAdded = "TOUR_EMERGENCY_EVIDENCE_ADDED";
    public const string TourEmergencyEvidenceWithdrawn = "TOUR_EMERGENCY_EVIDENCE_WITHDRAWN";
    public const string TourEmergencyStopReauthenticationFailed = "TOUR_EMERGENCY_STOP_REAUTH_FAILED";
    public const string WeatherSafetyReviewCreated = "WEATHER_SAFETY_REVIEW_CREATED";
    public const string WeatherSafetyReviewAcknowledged = "WEATHER_SAFETY_REVIEW_ACKNOWLEDGED";
    public const string WeatherSafetyReviewEscalated = "WEATHER_SAFETY_REVIEW_ESCALATED";
    public const string BookingItineraryMaterializationFailed = "BOOKING_ITINERARY_MATERIALIZATION_FAILED";

    // UC-05 Sign Out
    public const string AuthSignOut = "AUTH_SIGN_OUT";

    // UC-07 Change Password
    public const string AuthChangePassword = "AUTH_CHANGE_PASSWORD";

    public const string AlgorithmParametersUpdate = "UpdateAlgorithmParameters";
    public const string BookingCancel = "CancelBooking";
    public const string TravelGroupMemberRemoved = "TravelGroupMemberRemoved";
    // UC-59 BR-130: Administrator access to active trip details
    public const string ActiveTripDetailsViewed = "ViewActiveTripDetails";

    // UC-45 Export Revenue Report
    public const string RevenueExportRequested = "REVENUE_EXPORT_REQUESTED";

    // UC-64 BR-130: Staff access to payout detail, including beneficiary bank data
    public const string PayoutDetailsViewed = "ViewPayoutDetails";
    // UC-73/UC-74: Staff decision on a payout request
    public const string PayoutApproved = "ApprovePayoutRequest";
    public const string PayoutRejected = "RejectPayoutRequest";

    // DAILY-PAYOUT-TRANSFER, UC-73 (Task 9)
    public const string PayoutSettlementRetried = "PAYOUT_SETTLEMENT_RETRIED";
    public const string PayoutSettlementCarriedOver = "PAYOUT_SETTLEMENT_CARRIED_OVER";
    public const string PayoutSettlementManuallySettled = "PAYOUT_SETTLEMENT_MANUALLY_SETTLED";
    // DAILY-PAYOUT-TRANSFER, Task 7A (PD-17): a reconciliation lookup found a discrepancy
    public const string PayoutReconciliationDiscrepancyDetected = "PAYOUT_RECONCILIATION_DISCREPANCY_DETECTED";

    // UC-43 Check-in scan verification rejected
    public const string CheckInScanRejected = "CheckInScanRejected";
}