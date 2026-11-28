namespace TripMate.Domain.Entities;

/// <summary>Immutable ownership, including merchants retired by the same TO.</summary>
public sealed class OperatorPaymentMerchantClaim
{
    public const int FingerprintLength = 64;
    public const string KeyConstraintName = "PK_OperatorPaymentMerchantClaims";
    private OperatorPaymentMerchantClaim() { }
    public string Id { get; private set; } = string.Empty;
    public long ChannelId { get; private set; }
    public OperatorPaymentChannel Channel { get; private set; } = null!;
    public DateTimeOffset CreatedAtUtc { get; private set; }

    public static OperatorPaymentMerchantClaim Create(OperatorPaymentChannel channel, string fingerprint, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(channel);
        ArgumentException.ThrowIfNullOrWhiteSpace(fingerprint);
        if (fingerprint.Length != FingerprintLength || !fingerprint.All(Uri.IsHexDigit))
            throw new ArgumentException("Invalid merchant fingerprint.", nameof(fingerprint));
        // Navigation fixes up the FK when the channel is inserted in the same SaveChanges.
        return new() { Channel = channel, Id = fingerprint, CreatedAtUtc = now.ToUniversalTime() };
    }
}