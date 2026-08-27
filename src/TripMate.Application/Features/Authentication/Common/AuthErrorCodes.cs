namespace TripMate.Application.Features.Authentication.Common;

public static class AuthErrorCodes
{
    public const string InvalidCredentials = "auth.invalid_credentials";
    public const string AccountLocked = "auth.account_locked";
    public const string AccountInactive = "auth.account_inactive";
    public const string AccountRestricted = "auth.account_restricted";
    public const string EmailAlreadyRegistered = "auth.email_already_registered";
}
