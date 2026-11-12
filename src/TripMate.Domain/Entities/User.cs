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

    /// <summary>Provider identifier used for safe avatar replacement and durable cleanup.</summary>
    public string? AvatarPublicId { get; set; }

    public AccountStatus Status { get; set; } = AccountStatus.PendingEmailVerification;

    public DateTimeOffset? EmailVerifiedAtUtc { get; set; }

    public DateTimeOffset? PhoneVerifiedAtUtc { get; set; }

    public DateTimeOffset CreatedAtUtc { get; set; }

    public DateTimeOffset UpdatedAtUtc { get; set; }

    public DateTimeOffset? LastLoginAtUtc { get; set; }

    /// <summary>Set together with <see cref="LockedAtUtc"/> when this account is locked
    /// (DAILY-PAYOUT-TRANSFER spec v6, BR-189, PD-9A) — null while the account is not locked.</summary>
    public string? LockReason { get; set; }

    public DateTimeOffset? LockedAtUtc { get; set; }

    public ICollection<RefreshToken> RefreshTokens { get; set; } = new List<RefreshToken>();

    /// <summary>Locks this account for <paramref name="reason"/> (BR-189, PD-9A). The caller is
    /// responsible for terminating existing sessions (BR-175) — this only sets the account state
    /// that <see cref="Unlock"/> later clears.</summary>
    public void Lock(string reason, DateTimeOffset nowUtc)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);

        Status = AccountStatus.Locked;
        LockReason = reason;
        LockedAtUtc = nowUtc;
        UpdatedAtUtc = nowUtc;
    }

    /// <summary>Unlocks this account (UC-48, MSG199 is the caller's responsibility to enforce
    /// before calling this).</summary>
    public void Unlock(DateTimeOffset nowUtc)
    {
        Status = AccountStatus.Active;
        LockReason = null;
        LockedAtUtc = null;
        UpdatedAtUtc = nowUtc;
    }
}