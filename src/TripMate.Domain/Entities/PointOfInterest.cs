using TripMate.Domain.Common;
using TripMate.Domain.Enums;

namespace TripMate.Domain.Entities;

public class PointOfInterest : BaseEntity
{
    private readonly List<PoiOpeningHour> _openingHours = [];
    private readonly List<PoiTag> _poiTags = [];

    private PointOfInterest()
    {
    }

    public int CategoryId { get; private set; }

    public PoiCategory Category { get; private set; } = null!;

    public string Name { get; private set; } = string.Empty;

    public string? Description { get; private set; }

    public decimal Latitude { get; private set; }

    public decimal Longitude { get; private set; }

    public string? Address { get; private set; }

    public IndoorOutdoorType IndoorOutdoor { get; private set; }

    public decimal? ScenicScore { get; private set; }

    public decimal? PhotoRating { get; private set; }

    public int AverageVisitDurationMinutes { get; private set; }

    public bool HasShelter { get; private set; }

    public PointOfInterestStatus Status { get; private set; }

    public long? CreatedById { get; private set; }

    public DateTimeOffset CreatedAtUtc { get; private set; }

    public DateTimeOffset UpdatedAtUtc { get; private set; }

    public IReadOnlyCollection<PoiOpeningHour> OpeningHours => _openingHours.AsReadOnly();

    public IReadOnlyCollection<PoiTag> PoiTags => _poiTags.AsReadOnly();

    public static PointOfInterest Create(
        PoiCategory category,
        string name,
        decimal latitude,
        decimal longitude,
        long createdById,
        DateTimeOffset createdAtUtc,
        string? address = null,
        string? description = null,
        IndoorOutdoorType indoorOutdoor = IndoorOutdoorType.Outdoor,
        int averageVisitDurationMinutes = 60,
        bool hasShelter = false)
    {
        ArgumentNullException.ThrowIfNull(category);

        var normalizedName = NormalizeRequired(name, 200, nameof(name));
        var normalizedAddress = NormalizeOptional(address, 400, nameof(address));
        var normalizedDescription = NormalizeOptional(description, 2000, nameof(description));

        if (latitude is < -90 or > 90)
        {
            throw new ArgumentOutOfRangeException(nameof(latitude));
        }

        if (longitude is < -180 or > 180)
        {
            throw new ArgumentOutOfRangeException(nameof(longitude));
        }

        if (createdById <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(createdById));
        }

        if (averageVisitDurationMinutes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(averageVisitDurationMinutes));
        }

        if (!Enum.IsDefined(indoorOutdoor))
        {
            throw new ArgumentOutOfRangeException(nameof(indoorOutdoor));
        }

        return new PointOfInterest
        {
            Category = category,
            Name = normalizedName,
            Latitude = NormalizeCoordinate(latitude),
            Longitude = NormalizeCoordinate(longitude),
            Address = normalizedAddress,
            Description = normalizedDescription,
            IndoorOutdoor = indoorOutdoor,
            AverageVisitDurationMinutes = averageVisitDurationMinutes,
            HasShelter = hasShelter,
            Status = PointOfInterestStatus.Active,
            CreatedById = createdById,
            CreatedAtUtc = createdAtUtc,
            UpdatedAtUtc = createdAtUtc,
        };
    }

    public void AddOpeningHour(PoiOpeningHour openingHour)
    {
        ArgumentNullException.ThrowIfNull(openingHour);

        if (_openingHours.Any(existing => existing.DayOfWeek == openingHour.DayOfWeek))
        {
            throw new InvalidOperationException("Only one opening-hours entry is allowed per day.");
        }

        openingHour.AttachTo(this);
        _openingHours.Add(openingHour);
    }

    public void AddTag(Tag tag)
    {
        ArgumentNullException.ThrowIfNull(tag);

        var alreadyAdded = _poiTags.Any(mapping =>
            ReferenceEquals(mapping.Tag, tag)
            || (tag.Id != 0 && mapping.TagId == tag.Id));

        if (!alreadyAdded)
        {
            _poiTags.Add(PoiTag.Create(this, tag));
        }
    }

    private static decimal NormalizeCoordinate(decimal value) =>
        Math.Round(value, 6, MidpointRounding.AwayFromZero);

    private static string NormalizeRequired(string value, int maxLength, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("Value is required.", parameterName);
        }

        var normalized = value.Trim();
        if (normalized.Length > maxLength)
        {
            throw new ArgumentException($"Value cannot exceed {maxLength} characters.", parameterName);
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
            throw new ArgumentException($"Value cannot exceed {maxLength} characters.", parameterName);
        }

        return normalized;
    }
}