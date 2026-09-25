namespace TripMate.Api.Common;

public static class EmailVerificationResendRateLimiter
{
    public const string PolicyName = "email-verification-resend";
    public const int PermitLimit = 10;
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(1);
}