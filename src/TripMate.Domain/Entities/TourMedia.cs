using TripMate.Domain.Common;
using TripMate.Domain.Enums;

namespace TripMate.Domain.Entities;

/// <summary>
/// Tour-owned Cloudinary image metadata. Raw image bytes never belong to this entity.
/// </summary>
public sealed class TourMedia : BaseEntity
{
    public const int CloudinaryPublicIdMaxLength = 500;
    public const int DeliveryUrlMaxLength = 1000;
    public const int CaptionMaxLength = 500;
    public const int AltTextMaxLength = 500;
    public const int LifecycleStatusMaxLength = 16;
    public const string AltTextCollation = "Vietnamese_100_CI_AS";

    private TourMedia()
    {
    }

    public long TourId { get; private set; }

    public Tour Tour { get; private set; } = null!;

    public string CloudinaryPublicId { get; private set; } = string.Empty;

    public string DeliveryUrl { get; private set; } = string.Empty;

    public string? Caption { get; private set; }

    public int SortOrder { get; private set; }

    public bool IsPrimary { get; private set; }

    public TourMediaLifecycleStatus LifecycleStatus { get; private set; }

    public DateTimeOffset CreatedAtUtc { get; private set; }

    public DateTimeOffset UpdatedAtUtc { get; private set; }

    public DateTimeOffset? DeletedAtUtc { get; private set; }

    public string AltText { get; private set; } = string.Empty;

    public static TourMedia Create(
        Tour tour,
        string cloudinaryPublicId,
        string deliveryUrl,
        string? caption,
        string altText,
        int sortOrder,
        bool isPrimary,
        DateTimeOffset createdAtUtc)
    {
        ArgumentNullException.ThrowIfNull(tour);
        ValidateSortOrder(sortOrder);

        var createdAt = createdAtUtc.ToUniversalTime();
        return new TourMedia
        {
            Tour = tour,
            TourId = tour.Id,
            CloudinaryPublicId = NormalizeRequired(
                cloudinaryPublicId,
                CloudinaryPublicIdMaxLength,
                nameof(cloudinaryPublicId)),
            DeliveryUrl = NormalizeDeliveryUrl(deliveryUrl),
            Caption = NormalizeOptional(caption, CaptionMaxLength, nameof(caption)),
            AltText = NormalizeRequired(altText, AltTextMaxLength, nameof(altText)),
            SortOrder = sortOrder,
            IsPrimary = isPrimary,
            LifecycleStatus = TourMediaLifecycleStatus.Active,
            CreatedAtUtc = createdAt,
            UpdatedAtUtc = createdAt,
        };
    }

    public void UpdateMetadata(
        string? caption,
        string altText,
        DateTimeOffset updatedAtUtc)
    {
        EnsureActive();
        Caption = NormalizeOptional(caption, CaptionMaxLength, nameof(caption));
        AltText = NormalizeRequired(altText, AltTextMaxLength, nameof(altText));
        UpdatedAtUtc = updatedAtUtc.ToUniversalTime();
    }

    public void SetOrderAndPrimary(
        int sortOrder,
        bool isPrimary,
        DateTimeOffset updatedAtUtc)
    {
        EnsureActive();
        ValidateSortOrder(sortOrder);
        SortOrder = sortOrder;
        IsPrimary = isPrimary;
        UpdatedAtUtc = updatedAtUtc.ToUniversalTime();
    }

    public bool SoftDelete(DateTimeOffset deletedAtUtc)
    {
        if (LifecycleStatus == TourMediaLifecycleStatus.Deleted)
        {
            return false;
        }

        var deletedAt = deletedAtUtc.ToUniversalTime();
        LifecycleStatus = TourMediaLifecycleStatus.Deleted;
        IsPrimary = false;
        DeletedAtUtc = deletedAt;
        UpdatedAtUtc = deletedAt;
        return true;
    }

    private void EnsureActive()
    {
        if (LifecycleStatus != TourMediaLifecycleStatus.Active)
        {
            throw new InvalidOperationException("Deleted tour media cannot be modified.");
        }
    }

    private static void ValidateSortOrder(int sortOrder)
    {
        if (sortOrder <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sortOrder));
        }
    }

    private static string NormalizeDeliveryUrl(string value)
    {
        var normalized = NormalizeRequired(value, DeliveryUrlMaxLength, nameof(value));
        if (!Uri.TryCreate(normalized, UriKind.Absolute, out var uri) ||
            !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("A valid absolute HTTPS delivery URL is required.", nameof(value));
        }

        return normalized;
    }

    private static string NormalizeRequired(string value, int maxLength, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("A nonblank value is required.", parameterName);
        }

        var normalized = value.Trim();
        if (normalized.Length > maxLength)
        {
            throw new ArgumentException(
                $"The value cannot exceed {maxLength} characters.",
                parameterName);
        }

        return normalized;
    }

    private static string? NormalizeOptional(string? value, int maxLength, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var normalized = value.Trim();
        if (normalized.Length > maxLength)
        {
            throw new ArgumentException(
                $"The value cannot exceed {maxLength} characters.",
                parameterName);
        }

        return normalized;
    }
}