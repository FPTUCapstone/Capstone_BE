using TripMate.Domain.Common;

namespace TripMate.Domain.Entities;

/// <summary>
/// Maps dbo.RefreshTokens. TokenHash stores a hash of the opaque refresh token value, never the
/// raw token — the column is named token_hash for exactly that reason. The raw value is handed
/// to the client once (at issuance) and is not recoverable from the database afterwards.
/// </summary>
public class RefreshToken : BaseEntity
{
    public long UserId { get; set; }

    public User User { get; set; } = null!;

    public string TokenHash { get; set; } = string.Empty;

    public DateTimeOffset ExpiresAtUtc { get; set; }

    public DateTimeOffset? RevokedAtUtc { get; set; }

    public string? DeviceInfo { get; set; }

    public DateTimeOffset CreatedAtUtc { get; set; }

    public bool IsActive => RevokedAtUtc is null && DateTimeOffset.UtcNow < ExpiresAtUtc;
}
