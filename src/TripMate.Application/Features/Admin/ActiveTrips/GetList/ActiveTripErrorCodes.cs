namespace TripMate.Application.Features.Admin.ActiveTrips.GetList;

public static class ActiveTripErrorCodes
{
    public const string Forbidden = "ActiveTrips.Forbidden";
    public const string InvalidDateRange = "ActiveTrips.InvalidDateRange";
    public const string InvalidFilter = "ActiveTrips.InvalidFilter";

    // UC-59: unknown or non-active session (proposed MSG133 wording, not locked SRS content).
    public const string NotFound = "ActiveTrips.NotFound";

    public const string NotFoundMessage = "Active trip not found or no longer available for monitoring.";
}
