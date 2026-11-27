using TripMate.Application.Features.Authentication.Common;
using TripMate.Application.Features.Operator.Tours.Common;

namespace TripMate.Application.Features.Operator.Profile.Common;

/// <summary>Stable error codes and field keys for UC-34 (Update Tour Operator Profile).</summary>
public static class OperatorProfileErrorCodes
{
    /// <summary>The caller is not an approved, active Tour Operator (FE shows MSG126).</summary>
    public const string Forbidden = OperatorTourErrorCodes.AccessRequired;

    public const string NotFound = "operator_profile.not_found";
    public const string RequestInvalid = "operator_profile.request_invalid";
    public const string Unavailable = "operator_profile.unavailable";

    /// <summary>Success code of the update response. Deliberately not an MSG number: the SRS reuses MSG121
    /// for an unrelated message, so the client picks the success copy.</summary>
    public const string Updated = "operator_profile.updated";

    /// <summary>Field-level codes (UC-34 §3.8.1). MSG19 is the logo code of this use case; the global
    /// message appendix reuses the number for a Traveler message, and the UC-specific section wins.</summary>
    public const string Required = AuthErrorCodes.Msg01;
    public const string InvalidEmail = AuthErrorCodes.Msg02;
    public const string InvalidPhone = AuthErrorCodes.Msg04;
    public const string InvalidLogo = "MSG19";

    /// <summary>Property names exposed to FE, matching its <c>OperatorProfileValidationErrors</c> keys.</summary>
    public static class Fields
    {
        public const string BusinessName = "businessName";
        public const string BusinessDescription = "businessDescription";
        public const string BusinessAddress = "businessAddress";
        public const string ContactPhone = "contactPhone";
        public const string ContactEmail = "contactEmail";
        public const string Website = "website";
        public const string Logo = "logo";
    }
}