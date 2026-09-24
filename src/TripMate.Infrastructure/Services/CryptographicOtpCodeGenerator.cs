using System.Globalization;
using System.Security.Cryptography;

using TripMate.Application.Common.Interfaces;

namespace TripMate.Infrastructure.Services;

/// <summary>
/// CSPRNG-backed 6-digit OTP: RandomNumberGenerator.GetInt32(0, 1_000_000) formatted "D6"
/// with the invariant culture, so every value from "000000" to "999999" — including leading
/// zeros — is a valid representation. The raw code is transient: never logged, never
/// persisted; only its protected representation is stored.
/// </summary>
public sealed class CryptographicOtpCodeGenerator : IOtpCodeGenerator
{
    public string Generate() =>
        RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6", CultureInfo.InvariantCulture);
}