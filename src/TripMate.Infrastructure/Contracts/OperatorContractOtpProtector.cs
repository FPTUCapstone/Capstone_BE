using System.Globalization;
using System.Security.Cryptography;
using System.Text;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Features.Operator.Contracts.Common;

namespace TripMate.Infrastructure.Contracts;

/// <summary>Separates signing OTPs from all other uses of the shared protection primitive.</summary>
public sealed class OperatorContractOtpProtector(Func<IOtpProtectionService> protection) : IOperatorContractOtpProtector
{
    private const string Purpose = "operator-contract-signing-otp-v1";

    public string Protect(string otp, long userId, long contractId, string contentSha256, Guid challengeId, DateTimeOffset createdAtUtc)
    {
        if (!ValidBinding(otp, userId, contractId, contentSha256, challengeId))
            throw new ArgumentException("A valid signing challenge binding is required.");
        return protection().Protect(Binding(otp, userId, contractId, contentSha256, challengeId), userId, createdAtUtc);
    }

    public bool Verify(string otp, long userId, long contractId, string contentSha256, Guid challengeId,
        DateTimeOffset createdAtUtc, string protectedOtp)
    {
        if (!ValidBinding(otp, userId, contractId, contentSha256, challengeId) || string.IsNullOrEmpty(protectedOtp)) return false;
        // Configuration errors deliberately propagate rather than becoming a failed-code attempt.
        return protection().Verify(Binding(otp, userId, contractId, contentSha256, challengeId), userId, createdAtUtc, protectedOtp);
    }

    private static bool ValidBinding(string? otp, long userId, long contractId, string? contentSha256, Guid challengeId) =>
        userId > 0 && contractId > 0 && challengeId != Guid.Empty && otp is { Length: 6 }
        && otp.All(character => character is >= '0' and <= '9')
        && contentSha256 is { Length: 64 } && contentSha256.All(Uri.IsHexDigit);

    private static string Binding(string otp, long userId, long contractId, string contentSha256, Guid challengeId)
    {
        var payload = string.Create(CultureInfo.InvariantCulture,
            $"{Purpose}|{userId}|{contractId}|{contentSha256.ToLowerInvariant()}|{challengeId:N}|{otp}");
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload)));
    }
}