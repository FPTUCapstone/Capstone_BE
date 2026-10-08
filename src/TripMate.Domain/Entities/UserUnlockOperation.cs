using TripMate.Domain.Common;
using TripMate.Domain.Enums;

namespace TripMate.Domain.Entities;

/// <summary>Persists a completed UC-49 mutation for idempotent replay.</summary>
public sealed class UserUnlockOperation : BaseEntity
{
    private UserUnlockOperation()
    {
    }

    private UserUnlockOperation(
        long administratorUserId,
        string idempotencyKey,
        string requestHash,
        long targetUserId,
        AccountStatus restoredStatus,
        DateTimeOffset unlockedAtUtc)
    {
        if (administratorUserId <= 0 || targetUserId <= 0)
        {
            throw new ArgumentOutOfRangeException(administratorUserId <= 0
                ? nameof(administratorUserId)
                : nameof(targetUserId));
        }

        if (string.IsNullOrWhiteSpace(idempotencyKey) || idempotencyKey.Length > 128)
        {
            throw new ArgumentException("An idempotency key of at most 128 characters is required.", nameof(idempotencyKey));
        }

        if (string.IsNullOrWhiteSpace(requestHash) || requestHash.Length > 128)
        {
            throw new ArgumentException("A request hash of at most 128 characters is required.", nameof(requestHash));
        }

        if (!Enum.IsDefined(restoredStatus) || restoredStatus == AccountStatus.Locked)
        {
            throw new ArgumentOutOfRangeException(nameof(restoredStatus));
        }

        AdministratorUserId = administratorUserId;
        IdempotencyKey = idempotencyKey.Trim();
        RequestHash = requestHash.Trim();
        TargetUserId = targetUserId;
        RestoredStatus = restoredStatus;
        UnlockedAtUtc = unlockedAtUtc.ToUniversalTime();
    }

    public long AdministratorUserId { get; private set; }

    public string IdempotencyKey { get; private set; } = string.Empty;

    public string RequestHash { get; private set; } = string.Empty;

    public long TargetUserId { get; private set; }

    public AccountStatus RestoredStatus { get; private set; }

    public DateTimeOffset UnlockedAtUtc { get; private set; }

    public static UserUnlockOperation Create(
        long administratorUserId,
        string idempotencyKey,
        string requestHash,
        long targetUserId,
        AccountStatus restoredStatus,
        DateTimeOffset unlockedAtUtc) =>
        new(administratorUserId, idempotencyKey, requestHash, targetUserId, restoredStatus, unlockedAtUtc);
}