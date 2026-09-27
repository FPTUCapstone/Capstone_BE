using TripMate.Domain.Common;
using TripMate.Domain.Enums;

namespace TripMate.Domain.Entities;

/// <summary>
/// Persistent delayed Cloudinary cleanup work retained after TourMedia is physically removed.
/// </summary>
public sealed class TourMediaCleanupOutboxItem : BaseEntity
{
    public const int CloudinaryPublicIdMaxLength = 500;
    public const int StatusMaxLength = 16;
    public const int LastErrorCodeMaxLength = 100;
    public const int DefaultMaxAttempts = 8;
    public const int MaximumAllowedAttempts = 100;

    private TourMediaCleanupOutboxItem()
    {
    }

    public long? TourMediaId { get; private set; }

    public TourMedia? TourMedia { get; private set; }

    public string CloudinaryPublicId { get; private set; } = string.Empty;

    public TourMediaCleanupStatus Status { get; private set; }

    public DateTimeOffset NotBeforeAtUtc { get; private set; }

    public int AttemptCount { get; private set; }

    public int MaxAttempts { get; private set; }

    public Guid? LeaseToken { get; private set; }

    public DateTimeOffset? LeaseExpiresAtUtc { get; private set; }

    public string? LastErrorCode { get; private set; }

    public DateTimeOffset CreatedAtUtc { get; private set; }

    public DateTimeOffset UpdatedAtUtc { get; private set; }

    public DateTimeOffset? CompletedAtUtc { get; private set; }

    public static TourMediaCleanupOutboxItem Create(
        TourMedia tourMedia,
        DateTimeOffset notBeforeAtUtc,
        DateTimeOffset createdAtUtc,
        int maxAttempts = DefaultMaxAttempts)
    {
        ArgumentNullException.ThrowIfNull(tourMedia);
        if (tourMedia.LifecycleStatus != TourMediaLifecycleStatus.Deleted)
        {
            throw new InvalidOperationException("Cleanup can only be scheduled for deleted Tour media.");
        }

        if (maxAttempts is < 1 or > MaximumAllowedAttempts)
        {
            throw new ArgumentOutOfRangeException(nameof(maxAttempts));
        }

        var createdAt = createdAtUtc.ToUniversalTime();
        var notBefore = notBeforeAtUtc.ToUniversalTime();
        if (notBefore < createdAt)
        {
            throw new ArgumentOutOfRangeException(nameof(notBeforeAtUtc));
        }

        return new TourMediaCleanupOutboxItem
        {
            TourMedia = tourMedia,
            TourMediaId = tourMedia.Id,
            CloudinaryPublicId = tourMedia.CloudinaryPublicId,
            Status = TourMediaCleanupStatus.Pending,
            NotBeforeAtUtc = notBefore,
            AttemptCount = 0,
            MaxAttempts = maxAttempts,
            CreatedAtUtc = createdAt,
            UpdatedAtUtc = createdAt,
        };
    }

    /// <summary>
    /// Records cleanup for an uploaded provider asset when metadata persistence could not commit.
    /// The provider identity is retained without inventing a TourMedia row.
    /// </summary>
    public static TourMediaCleanupOutboxItem CreateForOrphanedProviderAsset(
        string cloudinaryPublicId,
        DateTimeOffset createdAtUtc,
        int maxAttempts = DefaultMaxAttempts)
    {
        if (maxAttempts is < 1 or > MaximumAllowedAttempts)
        {
            throw new ArgumentOutOfRangeException(nameof(maxAttempts));
        }

        var createdAt = createdAtUtc.ToUniversalTime();
        return new TourMediaCleanupOutboxItem
        {
            CloudinaryPublicId = NormalizePublicId(cloudinaryPublicId),
            Status = TourMediaCleanupStatus.Pending,
            NotBeforeAtUtc = createdAt,
            AttemptCount = 0,
            MaxAttempts = maxAttempts,
            CreatedAtUtc = createdAt,
            UpdatedAtUtc = createdAt,
        };
    }

    public void BeginAttempt(
        Guid leaseToken,
        DateTimeOffset leaseExpiresAtUtc,
        DateTimeOffset startedAtUtc)
    {
        EnsureStatus(TourMediaCleanupStatus.Pending);
        if (leaseToken == Guid.Empty)
        {
            throw new ArgumentException("A lease token is required.", nameof(leaseToken));
        }

        var startedAt = startedAtUtc.ToUniversalTime();
        var leaseExpiresAt = leaseExpiresAtUtc.ToUniversalTime();
        if (startedAt < NotBeforeAtUtc)
        {
            throw new InvalidOperationException("Cleanup is not due yet.");
        }

        if (AttemptCount >= MaxAttempts)
        {
            throw new InvalidOperationException("Cleanup has no attempts remaining.");
        }

        if (leaseExpiresAt <= startedAt)
        {
            throw new ArgumentOutOfRangeException(nameof(leaseExpiresAtUtc));
        }

        Status = TourMediaCleanupStatus.InProgress;
        AttemptCount++;
        LeaseToken = leaseToken;
        LeaseExpiresAtUtc = leaseExpiresAt;
        UpdatedAtUtc = startedAt;
    }

    public void ScheduleRetry(
        string errorCode,
        DateTimeOffset notBeforeAtUtc,
        DateTimeOffset updatedAtUtc)
    {
        EnsureStatus(TourMediaCleanupStatus.InProgress);
        if (AttemptCount >= MaxAttempts)
        {
            throw new InvalidOperationException("Cleanup must be exhausted after its final attempt.");
        }

        var updatedAt = updatedAtUtc.ToUniversalTime();
        var notBefore = notBeforeAtUtc.ToUniversalTime();
        if (notBefore <= updatedAt)
        {
            throw new ArgumentOutOfRangeException(nameof(notBeforeAtUtc));
        }

        Status = TourMediaCleanupStatus.Pending;
        NotBeforeAtUtc = notBefore;
        LastErrorCode = NormalizeErrorCode(errorCode);
        LeaseToken = null;
        LeaseExpiresAtUtc = null;
        UpdatedAtUtc = updatedAt;
    }

    public bool RecoverExpiredLease(DateTimeOffset recoveredAtUtc)
    {
        EnsureStatus(TourMediaCleanupStatus.InProgress);
        var recoveredAt = recoveredAtUtc.ToUniversalTime();
        if (LeaseExpiresAtUtc is null || LeaseExpiresAtUtc > recoveredAt)
        {
            throw new InvalidOperationException("The cleanup lease has not expired.");
        }

        if (AttemptCount >= MaxAttempts)
        {
            Exhaust("TOUR_MEDIA_CLEANUP_LEASE_EXPIRED", recoveredAt);
            return false;
        }

        Status = TourMediaCleanupStatus.Pending;
        NotBeforeAtUtc = recoveredAt;
        LeaseToken = null;
        LeaseExpiresAtUtc = null;
        UpdatedAtUtc = recoveredAt;
        return true;
    }

    public void Complete(DateTimeOffset completedAtUtc)
    {
        EnsureStatus(TourMediaCleanupStatus.InProgress);
        var completedAt = completedAtUtc.ToUniversalTime();
        Status = TourMediaCleanupStatus.Completed;
        LeaseToken = null;
        LeaseExpiresAtUtc = null;
        CompletedAtUtc = completedAt;
        UpdatedAtUtc = completedAt;
    }

    public void Exhaust(string errorCode, DateTimeOffset completedAtUtc)
    {
        EnsureStatus(TourMediaCleanupStatus.InProgress);
        if (AttemptCount is < 1 || AttemptCount > MaxAttempts)
        {
            throw new InvalidOperationException("Cleanup attempt count is outside its configured bounds.");
        }

        var completedAt = completedAtUtc.ToUniversalTime();
        Status = TourMediaCleanupStatus.Exhausted;
        LastErrorCode = NormalizeErrorCode(errorCode);
        LeaseToken = null;
        LeaseExpiresAtUtc = null;
        CompletedAtUtc = completedAt;
        UpdatedAtUtc = completedAt;
    }

    private void EnsureStatus(TourMediaCleanupStatus expected)
    {
        if (Status != expected)
        {
            throw new InvalidOperationException(
                $"Cleanup item must be {expected} before this transition.");
        }
    }

    private static string NormalizeErrorCode(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("A safe error code is required.", nameof(value));
        }

        var normalized = value.Trim();
        if (normalized.Length > LastErrorCodeMaxLength)
        {
            throw new ArgumentException(
                $"The error code cannot exceed {LastErrorCodeMaxLength} characters.",
                nameof(value));
        }

        return normalized;
    }

    private static string NormalizePublicId(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("A Cloudinary public identifier is required.", nameof(value));
        }

        var normalized = value.Trim();
        if (normalized.Length > CloudinaryPublicIdMaxLength)
        {
            throw new ArgumentException(
                $"The Cloudinary public identifier cannot exceed {CloudinaryPublicIdMaxLength} characters.",
                nameof(value));
        }

        return normalized;
    }
}