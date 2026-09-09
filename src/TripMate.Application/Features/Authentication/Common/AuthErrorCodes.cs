namespace TripMate.Application.Features.Authentication.Common;

public static class AuthErrorCodes
{
    public const string InvalidCredentials = "auth.invalid_credentials";
    public const string AccountPendingVerification = "auth.account_pending_verification";
    public const string AccountLocked = "auth.account_locked";
    public const string AccountInactive = "auth.account_inactive";
    public const string EmailAlreadyRegistered = "auth.email_already_registered";
}