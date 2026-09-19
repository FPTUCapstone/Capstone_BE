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

    public const string AlgorithmParametersUpdate = "UpdateAlgorithmParameters";
}