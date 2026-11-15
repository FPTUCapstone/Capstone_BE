namespace TripMate.Domain.Common;

public static class AuditEntityTypes
{
    public const string PointOfInterest = "POI";

    public const string OperatorProfile = "OperatorProfile";

    public const string TourMedia = "TourMedia";
    public const string Tour = "Tour";
    public const string TourSchedule = "TourSchedule";

    // UC-05 Sign Out
    public const string RefreshToken = "RefreshToken";

    // UC-07 Change Password
    public const string User = "User";

    public const string SystemConfig = "SystemConfig";
    public const string Booking = "Booking";
    public const string TravelGroup = "TravelGroup";

    // UC-59 BR-130
    public const string TripSession = "TripSession";

    // Weather alerts reviewed by Operators
    public const string WeatherSafetyReview = "WeatherSafetyReview";
    public const string WeatherEvent = "WeatherEvent";

    // UC-45 Export Revenue Report
    public const string RevenueExport = "RevenueExport";

    // UC-64/73/74 Staff Payout Request Review
    public const string Payout = "Payout";

    // DAILY-PAYOUT-TRANSFER (UC-72/73)
    public const string PayoutBatch = "PayoutBatch";

    // UC-43 Check-In
    public const string CheckIn = "CheckIn";

    // UC-63 Export Statistical Reports
    public const string StatisticalReportExport = "StatisticalReportExport";
}