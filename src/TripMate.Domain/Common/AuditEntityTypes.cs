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
    public const string Booking = "Booking";
    public const string TravelGroup = "TravelGroup";

    // UC-59 BR-130
    public const string TripSession = "TripSession";

    // UC-45 Export Revenue Report
    public const string RevenueExport = "RevenueExport";

    // UC-64/73/74 Staff Payout Request Review
    public const string Payout = "Payout";
}