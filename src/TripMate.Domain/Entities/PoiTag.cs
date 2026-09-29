namespace TripMate.Domain.Entities;

public class PoiTag
{
    private PoiTag()
    {
    }

    public long PointOfInterestId { get; private set; }

    public int TagId { get; private set; }

    public PointOfInterest PointOfInterest { get; private set; } = null!;

    public Tag Tag { get; private set; } = null!;

    internal static PoiTag Create(PointOfInterest pointOfInterest, Tag tag) =>
        new()
        {
            PointOfInterest = pointOfInterest ?? throw new ArgumentNullException(nameof(pointOfInterest)),
            Tag = tag ?? throw new ArgumentNullException(nameof(tag)),
        };
}