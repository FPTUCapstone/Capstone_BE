namespace TripMate.Domain.Common;

public static class AuditActionTypes
{
    public const string PoiCreate = "POI_CREATE";

    public const string OperatorApplicationApprove = "ApproveOperatorApplication";

    public const string OperatorApplicationReject = "RejectOperatorApplication";

    public const string TourMediaUpload = "TOUR_MEDIA_UPLOAD";
    public const string TourMediaMetadataUpdated = "TOUR_MEDIA_METADATA_UPDATED";
    public const string TourMediaReordered = "TOUR_MEDIA_REORDERED";
    public const string TourMediaDeleted = "TOUR_MEDIA_DELETED";

    // UC-05 Sign Out
    public const string AuthSignOut = "AUTH_SIGN_OUT";

    // UC-64 BR-130: Administrator access to payout details, including transfer information
    public const string PayoutDetailsViewed = "ViewPayoutDetails";
}