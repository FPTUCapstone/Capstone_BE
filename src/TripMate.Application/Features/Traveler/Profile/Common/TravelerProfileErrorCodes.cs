namespace TripMate.Application.Features.Traveler.Profile.Common;

public static class TravelerProfileErrorCodes
{
    public const string Forbidden = "traveler.profile_forbidden";
    public const string NotFound = "traveler.profile_not_found";
    public const string Required = "MSG01";
    public const string Invalid = "MSG02";
    public const string InvalidPhone = "MSG04";
    public const string PhoneAlreadyExists = Authentication.Common.AuthErrorCodes.MsgPhoneDup;
    public const string InvalidAvatar = "MSG19";
    public const string Success = "MSG20";
    public const string Unavailable = "MSG127";
}