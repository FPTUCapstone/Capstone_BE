using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

using Microsoft.Extensions.Options;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Common.Models;

namespace TripMate.Infrastructure.Security;

/// <summary>
/// UC-29 spec §6. The plaintext <c>{v, tid, n, iat}</c> carries no personal data. It is encrypted with
/// AES-256-GCM (associated data "TM1") into the token, and the token is signed with HMAC-SHA256:
/// content = <c>TM1.{base64url(nonce ‖ ciphertext ‖ tag)}.{base64url(hmac("TM1." + token))}</c>.
/// Verification checks the signature in constant time before decrypting, and returns false for any
/// malformed or forged input. Keys and plaintext are never logged.
/// </summary>
public sealed class AesGcmHmacTicketQrCodec : ITicketQrCodec
{
    public const string ContentPrefix = "TM1";
    private const int PayloadVersion = 1;
    private const int NonceSizeBytes = 12;
    private const int TagSizeBytes = 16;
    private const int TicketNonceSizeBytes = 16;
    private const char Separator = '.';

    private static readonly byte[] AssociatedData = Encoding.ASCII.GetBytes(ContentPrefix);

    private readonly byte[] encryptionKey;
    private readonly byte[] signingKey;

    public AesGcmHmacTicketQrCodec(IOptions<TicketQrOptions> options)
    {
        var value = options.Value;
        if (!value.HasUsableKeys()
            || !TicketQrOptions.TryDecodeKey(value.EncryptionKey, out encryptionKey)
            || !TicketQrOptions.TryDecodeKey(value.SigningKey, out signingKey))
        {
            throw new InvalidOperationException(TicketQrOptions.ValidationMessage);
        }
    }

    public TicketQrPayload Issue(long ticketId, DateTimeOffset issuedAtUtc)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(ticketId);

        var plaintext = JsonSerializer.SerializeToUtf8Bytes(new QrPlaintext(PayloadVersion, ticketId,
            Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(TicketNonceSizeBytes)),
            issuedAtUtc.ToUnixTimeSeconds()));

        var sealedToken = new byte[NonceSizeBytes + plaintext.Length + TagSizeBytes];
        var nonce = sealedToken.AsSpan(0, NonceSizeBytes);
        RandomNumberGenerator.Fill(nonce);
        using (var aes = new AesGcm(encryptionKey, TagSizeBytes))
        {
            aes.Encrypt(nonce, plaintext, sealedToken.AsSpan(NonceSizeBytes, plaintext.Length),
                sealedToken.AsSpan(NonceSizeBytes + plaintext.Length), AssociatedData);
        }

        var token = Base64Url.EncodeToString(sealedToken);
        var signature = Base64Url.EncodeToString(Sign(token));
        return new TicketQrPayload(token, signature, ContentFor(token, signature));
    }

    public string ContentFor(string qrToken, string signature) =>
        $"{ContentPrefix}{Separator}{qrToken}{Separator}{signature}";

    public bool TryVerify(string content, out TicketQrVerification? verification)
    {
        verification = null;
        var parts = content?.Split(Separator) ?? [];
        if (parts.Length != 3 || parts[0] != ContentPrefix || parts[1].Length == 0 || parts[2].Length == 0)
            return false;

        var token = parts[1];
        if (!Base64Url.IsValid(token) || !Base64Url.IsValid(parts[2]))
            return false;

        var signature = Base64Url.DecodeFromChars(parts[2]);
        if (!CryptographicOperations.FixedTimeEquals(signature, Sign(token)))
            return false;

        var sealedToken = Base64Url.DecodeFromChars(token);
        if (sealedToken.Length <= NonceSizeBytes + TagSizeBytes)
            return false;

        var plaintext = new byte[sealedToken.Length - NonceSizeBytes - TagSizeBytes];
        try
        {
            using var aes = new AesGcm(encryptionKey, TagSizeBytes);
            aes.Decrypt(sealedToken.AsSpan(0, NonceSizeBytes),
                sealedToken.AsSpan(NonceSizeBytes, plaintext.Length),
                sealedToken.AsSpan(NonceSizeBytes + plaintext.Length), plaintext, AssociatedData);

            var payload = JsonSerializer.Deserialize<QrPlaintext>(plaintext);
            if (payload is null || payload.Version != PayloadVersion || payload.TicketId <= 0)
                return false;

            verification = new TicketQrVerification(payload.TicketId, token,
                DateTimeOffset.FromUnixTimeSeconds(payload.IssuedAt));
            return true;
        }
        catch (Exception exception) when (exception is CryptographicException or JsonException
                                              or ArgumentOutOfRangeException)
        {
            return false;
        }
    }

    private byte[] Sign(string token) =>
        HMACSHA256.HashData(signingKey, Encoding.ASCII.GetBytes($"{ContentPrefix}{Separator}{token}"));

    private sealed record QrPlaintext(
        [property: JsonPropertyName("v")] int Version,
        [property: JsonPropertyName("tid")] long TicketId,
        [property: JsonPropertyName("n")] string Nonce,
        [property: JsonPropertyName("iat")] long IssuedAt);
}
