namespace TripMate.Infrastructure.Security;

/// <summary>
/// UC-29 QR keys. Secret: supply via User Secrets locally and environment variables on the host
/// (TicketQr__EncryptionKey, TicketQr__SigningKey) — never appsettings files, source control or logs.
/// ASP.NET Data Protection is deliberately not used: its key ring is not persisted on the hosts.
/// </summary>
public sealed class TicketQrOptions
{
    public const string SectionName = "TicketQr";
    public const int KeyLengthBytes = 32;
    public const string ValidationMessage =
        "TicketQr:EncryptionKey and TicketQr:SigningKey must each be a Base64-encoded 32-byte key, and they must differ.";

    public string EncryptionKey { get; set; } = string.Empty;

    public string SigningKey { get; set; } = string.Empty;

    public bool HasUsableKeys() =>
        TryDecodeKey(EncryptionKey, out var encryptionKey)
        && TryDecodeKey(SigningKey, out var signingKey)
        && !encryptionKey.AsSpan().SequenceEqual(signingKey);

    internal static bool TryDecodeKey(string? value, out byte[] key)
    {
        key = [];
        if (string.IsNullOrWhiteSpace(value))
            return false;

        var buffer = new byte[KeyLengthBytes + 1];
        if (!Convert.TryFromBase64String(value.Trim(), buffer, out var written) || written != KeyLengthBytes)
            return false;

        key = buffer[..KeyLengthBytes];
        return true;
    }
}