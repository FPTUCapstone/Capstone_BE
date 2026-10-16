namespace TripMate.Application.Features.Tours.Shared;

public static class TourErrorCodes
{
    /// <summary>
    /// The tour does not exist or is not publicly visible (MSG65). Both cases are deliberately
    /// indistinguishable so a draft or a hidden version cannot be probed.
    /// </summary>
    public const string NotFound = "tour.not_found";
}
