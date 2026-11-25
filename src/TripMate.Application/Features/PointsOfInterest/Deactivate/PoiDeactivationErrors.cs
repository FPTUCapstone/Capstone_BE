namespace TripMate.Application.Features.PointsOfInterest.Deactivate;

public static class PoiDeactivationErrors
{
    public const string Msg01 = "MSG01";
    public const string Msg123 = "MSG123";
    public const string Msg126 = "MSG126";
    public const string Msg127 = "MSG127";
    public const string Msg193 = "MSG193";
    public const string Msg194 = "MSG194";

    public const string NotFound = "POI.NotFound";
    public const string AlreadyInactive = "POI.AlreadyInactive";
    public const string ReferenceMismatch = "POI.ReferenceMismatch";
    public const string Forbidden = "POI.Forbidden";
    public const string InvalidInput = "POI.InvalidInput";
    public const string Unavailable = "POI.Unavailable";

    public const string Msg123Message = "Deactivation reason is required.";
    public const string Msg126Message = "You do not have permission to access this function.";
    public const string Msg127Message = "TripMate is temporarily unable to process your request. Please check your connection and try again.";
    public const string Msg193Message = "Point of interest deactivated.";
    public const string Msg194Message = "The point of interest is referenced by itineraries, routes or bookings.";
    public const string NotFoundMessage = "Point of interest not found.";
    public const string AlreadyInactiveMessage = "The point of interest is already inactive.";
    public const string ReferenceMismatchMessage = "Active references have changed since preview. Review current references and confirm.";
}