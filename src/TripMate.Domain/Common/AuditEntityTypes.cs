namespace TripMate.Domain.Common;

public static class AuditEntityTypes
{
    public const string PointOfInterest = "POI";

    public const string OperatorProfile = "OperatorProfile";

    public const string TourMedia = "TourMedia";
    public const string Tour = "Tour";

    // UC-05 Sign Out
    public const string RefreshToken = "RefreshToken";

    // UC-07 Change Password
    public const string User = "User";

    public const string SystemConfig = "SystemConfig";

    // UC-59 BR-130
    public const string TripSession = "TripSession";
}