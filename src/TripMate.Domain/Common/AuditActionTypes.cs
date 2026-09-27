namespace TripMate.Domain.Common;

public static class AuditActionTypes
{
    public const string PoiCreate = "POI_CREATE";

    public const string OperatorApplicationApprove = "ApproveOperatorApplication";

    public const string TourMediaUpload = "TOUR_MEDIA_UPLOAD";

    // UC-05 Sign Out
    public const string AuthSignOut = "AUTH_SIGN_OUT";
}