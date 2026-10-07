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

    public AccountStatus? StatusBeforeLock { get; private set; }

    public long? LockedByUserId { get; private set; }

    public DateTimeOffset? LockedAtUtc { get; private set; }

    public string? LockReason { get; private set; }

    public DateTimeOffset? EmailVerifiedAtUtc { get; set; }

    public DateTimeOffset? PhoneVerifiedAtUtc { get; set; }

    public DateTimeOffset CreatedAtUtc { get; set; }

    public DateTimeOffset UpdatedAtUtc { get; set; }

    public DateTimeOffset? LastLoginAtUtc { get; set; }

    public ICollection<RefreshToken> RefreshTokens { get; set; } = new List<RefreshToken>();

    public void RecordLock(
        AccountStatus statusBeforeLock,
        long lockedByUserId,
        DateTimeOffset lockedAtUtc,
        string lockReason)
    {
        if (!Enum.IsDefined(statusBeforeLock) || statusBeforeLock == AccountStatus.Locked)
        {
            throw new ArgumentOutOfRangeException(nameof(statusBeforeLock));
        }

        if (lockedByUserId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(lockedByUserId));
        }

        if (string.IsNullOrWhiteSpace(lockReason) || lockReason.Trim().Length > 1000)
        {
            throw new ArgumentException("A lock reason of at most 1,000 characters is required.", nameof(lockReason));
        }

        StatusBeforeLock = statusBeforeLock;
        LockedByUserId = lockedByUserId;
        LockedAtUtc = lockedAtUtc.ToUniversalTime();
        LockReason = lockReason.Trim();
        Status = AccountStatus.Locked;
    }

    public bool TryRestoreFromLock(out AccountStatus restoredStatus)
    {
        restoredStatus = Status;
        if (Status != AccountStatus.Locked
            || StatusBeforeLock is null
            || !Enum.IsDefined(StatusBeforeLock.Value)
            || StatusBeforeLock == AccountStatus.Locked
            || LockedByUserId is null
            || LockedAtUtc is null
            || string.IsNullOrWhiteSpace(LockReason))
        {
            return false;
        }

        restoredStatus = StatusBeforeLock.Value;
        Status = restoredStatus;
        StatusBeforeLock = null;
        LockedByUserId = null;
        LockedAtUtc = null;
        LockReason = null;
        return true;
    }
}