using TripMate.Domain.Common;

namespace TripMate.Domain.Entities;

public class PoiPhoto : BaseEntity
{
    public const int UrlMaxLength = 500;
    public const int CaptionMaxLength = 200;

    private PoiPhoto()
    {
    }

    public long PointOfInterestId { get; private set; }

    public PointOfInterest PointOfInterest { get; private set; } = null!;

    public string Url { get; private set; } = string.Empty;

    public string? Caption { get; private set; }

    public int SortOrder { get; private set; }

    public static PoiPhoto Create(
        PointOfInterest pointOfInterest,
        string url,
        string? caption = null,
        int sortOrder = 0)
    {
        ArgumentNullException.ThrowIfNull(pointOfInterest);

        if (string.IsNullOrWhiteSpace(url))
        {
            throw new ArgumentException("Photo URL is required.", nameof(url));
        }

        var normalizedUrl = url.Trim();
        if (normalizedUrl.Length > UrlMaxLength)
        {
            throw new ArgumentException($"Photo URL cannot exceed {UrlMaxLength} characters.", nameof(url));
        }

        string? normalizedCaption = null;
        if (!string.IsNullOrWhiteSpace(caption))
        {
            normalizedCaption = caption.Trim();
            if (normalizedCaption.Length > CaptionMaxLength)
            {
                throw new ArgumentException($"Caption cannot exceed {CaptionMaxLength} characters.", nameof(caption));
            }
        }

        return new PoiPhoto
        {
            PointOfInterest = pointOfInterest,
            PointOfInterestId = pointOfInterest.Id,
            Url = normalizedUrl,
            Caption = normalizedCaption,
            SortOrder = sortOrder,
        };
    }
}