namespace TripMate.Api.Common;

/// <summary>
/// Account-independent per-IP abuse limiter for the UC-06 password-reset endpoints. It is
/// deliberately decoupled from the account-level 60-second resend cooldown, which always
/// returns the generic 200 response and never an HTTP 429.
/// </summary>
public static class PasswordResetRateLimiter
{
    public const string PolicyName = "password-reset";

    public const int PermitLimit = 10;

    public static readonly TimeSpan Window = TimeSpan.FromMinutes(1);
}