using FluentValidation;

using TripMate.Domain.Entities;

namespace TripMate.Application.Features.TripReviews.Common;

public static class TripReviewInputRules
{
    public const int MinimumRating = 1;
    public const int MaximumRating = 5;
    public const int MaximumPhotos = 5;
    public const long MaximumPhotoBytes = 5_000_000;
    public const int RowVersionBytes = 8;
    public const string JpegMime = "image/jpeg";
    public const string PngMime = "image/png";
    public const string WebpMime = "image/webp";

    public static bool IsRating(int value) => value is >= MinimumRating and <= MaximumRating;

    public static bool IsCanonicalVersion(string? value)
    {
        if (value is null) return false;
        Span<byte> bytes = stackalloc byte[RowVersionBytes];
        return Convert.TryFromBase64String(value, bytes, out var written)
            && written == RowVersionBytes && Convert.ToBase64String(bytes) == value;
    }

    public static bool IsPhotoMetadata(TripReviewPhotoInput? photo)
    {
        if (photo is null || string.IsNullOrWhiteSpace(photo.FileName) || string.IsNullOrWhiteSpace(photo.ContentType)) return false;
        // File parts are local filenames, never delivery URLs, provider IDs or paths.
        if (photo.FileName.IndexOfAny(['/', '\\', ':']) >= 0) return false;
        var extension = Path.GetExtension(photo.FileName);
        return photo.ContentType switch
        {
            JpegMime => extension.Equals(".jpg", StringComparison.OrdinalIgnoreCase) || extension.Equals(".jpeg", StringComparison.OrdinalIgnoreCase),
            PngMime => extension.Equals(".png", StringComparison.OrdinalIgnoreCase),
            WebpMime => extension.Equals(".webp", StringComparison.OrdinalIgnoreCase),
            _ => false,
        };
    }
}

internal sealed class ReviewTextValidator : AbstractValidator<ReviewText>
{
    public ReviewTextValidator()
    {
        RuleFor(text => text.Title).NotEmpty().WithErrorCode(TripReviewErrorCodes.InvalidInput).MaximumLength(TripReview.TitleMaxLength).WithErrorCode(TripReviewErrorCodes.InvalidInput);
        RuleFor(text => text.Content).NotEmpty().WithErrorCode(TripReviewErrorCodes.InvalidInput).MaximumLength(TripReview.ContentMaxLength).WithErrorCode(TripReviewErrorCodes.InvalidInput);
    }
}

public sealed record TripReviewPoiInput(long PoiId, int Rating);

/// <summary>Structural input only; raw bytes still require Task 8's bounded inspection before upload.</summary>
public sealed record TripReviewPhotoInput(string FileName, string ContentType, long DeclaredLength, ReadOnlyMemory<byte> Bytes);