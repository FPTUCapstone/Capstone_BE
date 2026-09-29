using TripMate.Domain.Common;
using TripMate.Domain.Enums;

namespace TripMate.Domain.Entities;

/// <summary>Maps dbo.Users in database/tripmate_schema_v7.sql — see UserConfiguration.</summary>
public class User : BaseEntity
{
    public UserRole Role { get; set; }

    public string? Email { get; set; }

    public string? PhoneNumber { get; set; }

    /// <summary>Null for a social-login-only account (no password ever set).</summary>
    public string? PasswordHash { get; set; }

    public string FullName { get; set; } = string.Empty;

    public string? AvatarUrl { get; set; }

    public AccountStatus Status { get; set; } = AccountStatus.PendingEmailVerification;

    public DateTimeOffset? EmailVerifiedAtUtc { get; set; }

    public DateTimeOffset? PhoneVerifiedAtUtc { get; set; }

    public DateTimeOffset CreatedAtUtc { get; set; }

    public DateTimeOffset UpdatedAtUtc { get; set; }

    public DateTimeOffset? LastLoginAtUtc { get; set; }

    public ICollection<RefreshToken> RefreshTokens { get; set; } = new List<RefreshToken>();
}