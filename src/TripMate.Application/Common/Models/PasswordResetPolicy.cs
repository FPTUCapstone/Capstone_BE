namespace TripMate.Application.Common.Models;

/// <summary>
/// Approved UC-06 password-reset product rules. Single source of truth for the
/// password-reset policy values; the Infrastructure reset-state store and all
/// Task-1 tests must reference these constants instead of duplicating them.
/// </summary>
public static class PasswordResetPolicy
{
    /// <summary>An outstanding OTP/reset generation expires exactly this long after CreatedAtUtc.</summary>
    public static readonly TimeSpan OtpTimeToLive = TimeSpan.FromMinutes(3);

    /// <summary>Minimum time between two accepted OTP issuances for one account.</summary>
    public static readonly TimeSpan ResendCooldown = TimeSpan.FromSeconds(60);

    /// <summary>Reaching this many real wrong-code attempts invalidates the current reset generation.</summary>
    public const int MaxFailedAttempts = 5;
}