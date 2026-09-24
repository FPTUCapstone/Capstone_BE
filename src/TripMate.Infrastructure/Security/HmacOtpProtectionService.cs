using System.Globalization;
using System.Security.Cryptography;
using System.Text;

using Microsoft.Extensions.Options;

using TripMate.Application.Common.Interfaces;

namespace TripMate.Infrastructure.Security;

/// <summary>
/// HMAC-SHA256 OTP protection keyed by the configured pepper. The MAC input is a versioned,
/// delimited canonical payload — <c>v1|userId|canonical UTC timestamp|otp</c> — with the
/// timestamp normalized to UTC and formatted with the invariant round-trip "O" specifier, so
/// protect and verify derive identical bytes regardless of culture or source offset. Every
/// field is self-delimiting (userId is an integer, the timestamp contains no '|', the OTP is
/// digits only), so the boundaries are unambiguous. Verification uses
/// <see cref="CryptographicOperations.FixedTimeEquals"/> and returns false — never throws —
/// for any mismatch or malformed input. The raw OTP and the pepper are never logged.
/// </summary>
public sealed class HmacOtpProtectionService : IOtpProtectionService
{
    private const string PayloadVersion = "v1";

    private readonly byte[] pepper;

    public HmacOtpProtectionService(IOptions<PasswordResetSecurityOptions> options)
    {
        var otpPepper = options.Value.OtpPepper;
        if (string.IsNullOrWhiteSpace(otpPepper))
        {
            // Fail closed: refuse to protect OTPs with an unusable pepper. The message
            // carries no secret material.
            throw new InvalidOperationException(
                "PasswordResetSecurity:OtpPepper is not configured; password reset cannot protect OTPs.");
        }

        pepper = Encoding.UTF8.GetBytes(otpPepper);
    }

    public string Protect(string otp, long userId, DateTimeOffset createdAtUtc) =>
        Convert.ToHexString(ComputeMac(otp, userId, createdAtUtc));

    public bool Verify(string otp, long userId, DateTimeOffset createdAtUtc, string protectedOtp)
    {
        if (string.IsNullOrEmpty(otp) || string.IsNullOrEmpty(protectedOtp))
        {
            return false;
        }

        byte[] provided;
        try
        {
            provided = Convert.FromHexString(protectedOtp);
        }
        catch (FormatException)
        {
            return false;
        }

        var expected = ComputeMac(otp, userId, createdAtUtc);
        return CryptographicOperations.FixedTimeEquals(expected, provided);
    }

    private byte[] ComputeMac(string otp, long userId, DateTimeOffset createdAtUtc)
    {
        var payload = string.Create(
            CultureInfo.InvariantCulture,
            $"{PayloadVersion}|{userId}|{CanonicalTimestamp(createdAtUtc)}|{otp}");

        return HMACSHA256.HashData(pepper, Encoding.UTF8.GetBytes(payload));
    }

    private static string CanonicalTimestamp(DateTimeOffset createdAtUtc) =>
        createdAtUtc.UtcDateTime.ToString("O", CultureInfo.InvariantCulture);
}