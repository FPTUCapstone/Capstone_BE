using TripMate.Domain.Common;

namespace TripMate.Domain.Entities;

public class Review : BaseEntity
{
    public const int TargetTypeMaxLength = 14;
    public const int CommentMaxLength = 1_000;
    public const string TargetTypePoi = "POI";

    private Review()
    {
    }

    public long TravelerUserId { get; private set; }

    public string TargetType { get; private set; } = string.Empty;

    public long TargetId { get; private set; }

    public long? BookingId { get; private set; }

    public byte Rating { get; private set; }

    public byte? ScenicRating { get; private set; }

    public byte? PhotoRating { get; private set; }

    public string? Comment { get; private set; }

    public DateTimeOffset CreatedAtUtc { get; private set; }

    public static Review Create(
        long travelerUserId,
        string targetType,
        long targetId,
        byte rating,
        DateTimeOffset createdAtUtc,
        byte? scenicRating = null,
        byte? photoRating = null,
        string? comment = null,
        long? bookingId = null)
    {
        if (travelerUserId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(travelerUserId));
        }

        if (string.IsNullOrWhiteSpace(targetType))
        {
            throw new ArgumentException("Target type is required.", nameof(targetType));
        }

        var normalizedTargetType = targetType.Trim();
        if (normalizedTargetType.Length > TargetTypeMaxLength)
        {
            throw new ArgumentException($"Target type cannot exceed {TargetTypeMaxLength} characters.", nameof(targetType));
        }

        if (targetId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(targetId));
        }

        if (rating is < 1 or > 5)
        {
            throw new ArgumentOutOfRangeException(nameof(rating), "Rating must be between 1 and 5.");
        }

        if (scenicRating is not null && (scenicRating < 1 || scenicRating > 5))
        {
            throw new ArgumentOutOfRangeException(nameof(scenicRating), "Scenic rating must be between 1 and 5.");
        }

        if (photoRating is not null && (photoRating < 1 || photoRating > 5))
        {
            throw new ArgumentOutOfRangeException(nameof(photoRating), "Photo rating must be between 1 and 5.");
        }

        if (normalizedTargetType != TargetTypePoi && (scenicRating is not null || photoRating is not null))
        {
            throw new ArgumentException("Scenic and photo ratings are only supported for POI reviews.");
        }

        string? normalizedComment = null;
        if (!string.IsNullOrWhiteSpace(comment))
        {
            normalizedComment = comment.Trim();
            if (normalizedComment.Length > CommentMaxLength)
            {
                throw new ArgumentException($"Comment cannot exceed {CommentMaxLength} characters.", nameof(comment));
            }
        }

        return new Review
        {
            TravelerUserId = travelerUserId,
            TargetType = normalizedTargetType,
            TargetId = targetId,
            BookingId = bookingId,
            Rating = rating,
            ScenicRating = scenicRating,
            PhotoRating = photoRating,
            Comment = normalizedComment,
            CreatedAtUtc = createdAtUtc,
        };
    }
}