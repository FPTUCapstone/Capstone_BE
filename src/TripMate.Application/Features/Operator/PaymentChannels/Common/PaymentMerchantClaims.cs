using System.Security.Cryptography;
using System.Text;

using Microsoft.EntityFrameworkCore;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Common.Models;
using TripMate.Domain.Entities;

namespace TripMate.Application.Features.Operator.PaymentChannels.Common;

internal static class PaymentMerchantClaims
{
    // Fingerprint identifies a high-entropy Client ID, never an API/Checksum authentication key.
    // It is internal-only and cannot be used to call payOS. Actual credentials remain encrypted.
    private static string Fingerprint(string clientId) => Convert.ToHexStringLower(
        SHA256.HashData(Encoding.UTF8.GetBytes("payos-merchant:v1:" + clientId.Trim().ToLowerInvariant())));

    internal static async Task<Result<bool>> EnsureAsync(IApplicationDbContext db, IPaymentCredentialProtector protector,
        OperatorPaymentChannel channel, string clientId, DateTimeOffset now, CancellationToken token)
    {
        var fingerprint = Fingerprint(clientId);
        var existing = await db.OperatorPaymentMerchantClaims.AsNoTracking().SingleOrDefaultAsync(item => item.Id == fingerprint, token);
        if (existing is not null && (existing.ChannelId != channel.Id || channel.Id <= 0)) return InUse();

        var olderChannels = await db.OperatorPaymentChannels.AsNoTracking()
            .Where(item => item.TourOperatorUserId != channel.TourOperatorUserId && item.ClientIdRef != null)
            .Select(item => item.ClientIdRef!).ToListAsync(token);
        try
        {
            if (olderChannels.Any(reference => Fingerprint(protector.Unprotect(reference)) == fingerprint)) return InUse();
        }
        catch (Exception exception) when (exception is CryptographicException or FormatException)
        {
            return Result.Failure<bool>(PaymentChannelErrorCodes.VerificationFailed, "Existing channels require credential reconciliation before ownership can be established.");
        }
        if (existing is not null) return Result.Success(true);
        var local = db.OperatorPaymentMerchantClaims.Local.SingleOrDefault(item => item.Id == fingerprint);
        if (local is not null) return ReferenceEquals(local.Channel, channel) ? Result.Success(true) : InUse();
        db.OperatorPaymentMerchantClaims.Add(OperatorPaymentMerchantClaim.Create(channel, fingerprint, now));
        return Result.Success(true);
    }

    private static Result<bool> InUse() => Result.Failure<bool>(PaymentChannelErrorCodes.MerchantInUse,
        "This payOS merchant is already reserved for another operator.");
}
