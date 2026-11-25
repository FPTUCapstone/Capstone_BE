using TripMate.Domain.Common;
using TripMate.Domain.Enums;

namespace TripMate.Domain.Entities;

/// <summary>Maps dbo.Users in database/tripmate_schema_v7.sql — see UserConfiguration.</summary>
public class User : BaseEntity
{
    public const string SessionVersionClaim = "session_version";
    public UserRole Role { get; set; }

    public string? Email { get; set; }

    public string? PhoneNumber { get; set; }

    /// <summary>Null for a social-login-only account (no password ever set).</summary>
    public string? PasswordHash { get; set; }

    public bool MustChangePassword { get; set; }

    public string FullName { get; set; } = string.Empty;

    public string? AvatarUrl { get; set; }

    /// <summary>Provider identifier used for safe avatar replacement and durable cleanup.</summary>
    public string? AvatarPublicId { get; set; }

    public AccountStatus Status { get; set; } = AccountStatus.PendingEmailVerification;

    public AccountStatus? StatusBeforeLock { get; private set; }

    public long? LockedByUserId { get; private set; }

    public DateTimeOffset? EmailVerifiedAtUtc { get; set; }

    public DateTimeOffset? PhoneVerifiedAtUtc { get; set; }

    public DateTimeOffset CreatedAtUtc { get; set; }

    public DateTimeOffset UpdatedAtUtc { get; set; }

    public DateTimeOffset? LastLoginAtUtc { get; set; }

    /// <summary>Set together with <see cref="LockedAtUtc"/> when this account is locked
    /// (DAILY-PAYOUT-TRANSFER spec v6, BR-189, PD-9A) — null while the account is not locked.</summary>
    public string? LockReason { get; set; }

    public DateTimeOffset? LockedAtUtc { get; set; }

    public int SessionVersion { get; private set; }

    public ICollection<RefreshToken> RefreshTokens { get; set; } = new List<RefreshToken>();

    public bool TryActivateAfterContractSignature(DateTimeOffset nowUtc)
    {
        if (Role != UserRole.TourOperator || Status != AccountStatus.PendingContractSignature) return false;
        Status = AccountStatus.Active;
        SessionVersion = checked(SessionVersion + 1);
        UpdatedAtUtc = nowUtc.ToUniversalTime();
        return true;
    }

    /// <summary>Locks this account for <paramref name="reason"/> (BR-189, PD-9A). The caller is
    /// responsible for terminating existing sessions (BR-175) — this only sets the account state
    /// that <see cref="Unlock"/> later clears.</summary>
    public void Lock(string reason, DateTimeOffset nowUtc, long? lockedByUserId = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);

        if (Status != AccountStatus.Locked) StatusBeforeLock = Status;
        if (lockedByUserId.HasValue) LockedByUserId = lockedByUserId.Value;
        SessionVersion = checked(SessionVersion + 1);
        Status = AccountStatus.Locked;
        LockReason = reason;
        LockedAtUtc = nowUtc;
        UpdatedAtUtc = nowUtc;
    }

    /// <summary>Unlocks this account (UC-48, MSG199 is the caller's responsibility to enforce
    /// before calling this).</summary>
    public void Unlock(DateTimeOffset nowUtc)
    {
        Status = StatusBeforeLock ?? AccountStatus.Active;
        StatusBeforeLock = null;
        LockedByUserId = null;
        LockReason = null;
        LockedAtUtc = null;
        UpdatedAtUtc = nowUtc;
    }

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
        SessionVersion = checked(SessionVersion + 1);
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