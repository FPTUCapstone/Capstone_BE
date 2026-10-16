namespace TripMate.Domain.Common;

public static class AuditActionTypes
{
    public const string PoiCreate = "POI_CREATE";

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

    // UC-05 Sign Out
    public const string AuthSignOut = "AUTH_SIGN_OUT";

    // UC-07 Change Password
    public const string AuthChangePassword = "AUTH_CHANGE_PASSWORD";

    public const string AlgorithmParametersUpdate = "UpdateAlgorithmParameters";
    // UC-59 BR-130: Administrator access to active trip details
    public const string ActiveTripDetailsViewed = "ViewActiveTripDetails";
}