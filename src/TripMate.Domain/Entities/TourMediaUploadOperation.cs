using TripMate.Domain.Common;
using TripMate.Domain.Enums;

namespace TripMate.Domain.Entities;

/// <summary>
/// Durable idempotency state for one server-side Tour image upload.
/// </summary>
public sealed class TourMediaUploadOperation : BaseEntity
{
    public const int PayloadFingerprintLength = 64;
    public const int CloudinaryPublicIdMaxLength = 500;
    public const int StatusMaxLength = 16;
    public const string FingerprintCollation = "Latin1_General_100_BIN2";

    private TourMediaUploadOperation()
    {
    }

    public long ActorUserId { get; private set; }

    public User ActorUser { get; private set; } = null!;

    public long TourId { get; private set; }

    public Tour Tour { get; private set; } = null!;

    public Guid IdempotencyKey { get; private set; }

    public string PayloadFingerprint { get; private set; } = string.Empty;

    public string CloudinaryPublicId { get; private set; } = string.Empty;

    public TourMediaUploadOperationStatus Status { get; private set; }

    public long? TourMediaId { get; private set; }

    public DateTimeOffset? ProviderUploadedAtUtc { get; private set; }

    public DateTimeOffset? CompletedAtUtc { get; private set; }

    public DateTimeOffset CreatedAtUtc { get; private set; }

    public DateTimeOffset UpdatedAtUtc { get; private set; }

    public static TourMediaUploadOperation Create(
        User actorUser,
        Tour tour,
        Guid idempotencyKey,
        string payloadFingerprint,
        string cloudinaryPublicId,
        DateTimeOffset createdAtUtc)
    {
        ArgumentNullException.ThrowIfNull(actorUser);
        ArgumentNullException.ThrowIfNull(tour);
        if (idempotencyKey == Guid.Empty)
        {
            throw new ArgumentException("An idempotency key is required.", nameof(idempotencyKey));
        }

        var createdAt = createdAtUtc.ToUniversalTime();
        return new TourMediaUploadOperation
        {
            ActorUser = actorUser,
            ActorUserId = actorUser.Id,
            Tour = tour,
            TourId = tour.Id,
            IdempotencyKey = idempotencyKey,
            PayloadFingerprint = NormalizeFingerprint(payloadFingerprint),
            CloudinaryPublicId = NormalizePublicId(cloudinaryPublicId),
            Status = TourMediaUploadOperationStatus.Pending,
            CreatedAtUtc = createdAt,
            UpdatedAtUtc = createdAt,
        };
    }

    public void MarkProviderUploaded(DateTimeOffset uploadedAtUtc)
    {
        EnsureStatus(TourMediaUploadOperationStatus.Pending);
        var uploadedAt = uploadedAtUtc.ToUniversalTime();
        Status = TourMediaUploadOperationStatus.Uploaded;
        ProviderUploadedAtUtc = uploadedAt;
        UpdatedAtUtc = uploadedAt;
    }

    public void Complete(long tourMediaId, DateTimeOffset completedAtUtc)
    {
        EnsureStatus(TourMediaUploadOperationStatus.Uploaded);
        if (tourMediaId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(tourMediaId));
        }

        var completedAt = completedAtUtc.ToUniversalTime();
        Status = TourMediaUploadOperationStatus.Completed;
        TourMediaId = tourMediaId;
        CompletedAtUtc = completedAt;
        UpdatedAtUtc = completedAt;
    }

    private void EnsureStatus(TourMediaUploadOperationStatus expected)
    {
        if (Status != expected)
        {
            throw new InvalidOperationException(
                $"Upload operation must be {expected} before this transition.");
        }
    }

    private static string NormalizeFingerprint(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("A payload fingerprint is required.", nameof(value));
        }

        var normalized = value.Trim().ToUpperInvariant();
        if (normalized.Length != PayloadFingerprintLength ||
            normalized.Any(character => !Uri.IsHexDigit(character)))
        {
            throw new ArgumentException(
                $"The payload fingerprint must be {PayloadFingerprintLength} hexadecimal characters.",
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