namespace TripMate.Domain.Common;

public static class AuditEntityTypes
{
    public const string PointOfInterest = "POI";

    public const string OperatorProfile = "OperatorProfile";

    public const string TourMedia = "TourMedia";

    // UC-05 Sign Out
    public const string RefreshToken = "RefreshToken";

    // UC-59 BR-130
    public const string TripSession = "TripSession";
}