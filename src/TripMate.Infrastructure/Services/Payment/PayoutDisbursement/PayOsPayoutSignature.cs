using System.Security.Cryptography;
using System.Text;

namespace TripMate.Infrastructure.Services.Payment.PayoutDisbursement;

/// <summary>
/// Request signing for the PayOS Payouts API, mirroring the official PayOS SDK ("header" signature
/// type): the body properties sorted by name, each written as <c>name=value</c> with both parts
/// URL-encoded, joined with <c>&amp;</c>, then HMAC-SHA256 with the payout channel's checksum key
/// as lowercase hex. A property that is null is signed as an empty value.
/// </summary>
public static class PayOsPayoutSignature
{
    public static string SignedData(IReadOnlyDictionary<string, string> fields)
    {
        ArgumentNullException.ThrowIfNull(fields);
        return string.Join(
            "&",
            fields
                .OrderBy(field => field.Key, StringComparer.Ordinal)
                .Select(field => $"{Uri.EscapeDataString(field.Key)}={Uri.EscapeDataString(field.Value ?? string.Empty)}"));
    }

    public static string Compute(string checksumKey, IReadOnlyDictionary<string, string> fields)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(checksumKey);
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(checksumKey));
        return Convert.ToHexStringLower(hmac.ComputeHash(Encoding.UTF8.GetBytes(SignedData(fields))));
    }

    /// <summary>
    /// The provider documents a format check on <c>x-idempotency-key</c> and its SDK sends a UUID.
    /// The team's stable per-payout key is therefore mapped to a deterministic name-based UUID, so
    /// every retry of the same payout sends the same wire value and different payouts never share one.
    /// </summary>
    public static string IdempotencyHeaderValue(string idempotencyKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(idempotencyKey);
        Span<byte> bytes = stackalloc byte[16];
        SHA256.HashData(Encoding.UTF8.GetBytes(idempotencyKey)).AsSpan(0, 16).CopyTo(bytes);
        bytes[6] = (byte)((bytes[6] & 0x0F) | 0x50);
        bytes[8] = (byte)((bytes[8] & 0x3F) | 0x80);
        return new Guid(bytes, bigEndian: true).ToString();
    }
}