namespace TripMate.Application.Common.Interfaces;

/// <summary>
/// Protects a transient OTP by binding it to its owner account and creation time, so only
/// the in-memory reset state's stored protected value can later prove possession. The
/// protected representation is the only OTP form that survives beyond the request; raw
/// OTPs and the pepper are never logged or persisted.
/// </summary>
public interface IOtpProtectionService
{
    /// <summary>
    /// Returns the protected representation of <paramref name="otp"/> bound to
    /// <paramref name="userId"/> and the canonical UTC form of <paramref name="createdAtUtc"/>.
    /// </summary>
    string Protect(string otp, long userId, DateTimeOffset createdAtUtc);

    /// <summary>
    /// Recomputes the protected value over the same binding and compares it in fixed time.
    /// Returns false for any mismatch, stale binding, or malformed input — never throws.
    /// </summary>
    bool Verify(string otp, long userId, DateTimeOffset createdAtUtc, string protectedOtp);
}