namespace TripMate.Domain.Entities;

public class PoiOpeningHour
{
    private PoiOpeningHour()
    {
    }

    public long PointOfInterestId { get; private set; }

    public byte DayOfWeek { get; private set; }

    public TimeOnly? OpenTime { get; private set; }

    public TimeOnly? CloseTime { get; private set; }

    public bool IsClosed { get; private set; }

    public PointOfInterest PointOfInterest { get; private set; } = null!;

    public static PoiOpeningHour Create(
        byte dayOfWeek,
        TimeOnly? openTime,
        TimeOnly? closeTime,
        bool isClosed)
    {
        if (dayOfWeek > 6)
        {
            throw new ArgumentOutOfRangeException(nameof(dayOfWeek), "Day must be between 0 and 6.");
        }

        if (isClosed)
        {
            if (openTime is not null || closeTime is not null)
            {
                throw new ArgumentException("A closed day cannot contain opening times.");
            }
        }
        else
        {
            if (openTime is null || closeTime is null)
            {
                throw new ArgumentException("An open day requires both opening and closing times.");
            }

            if (openTime >= closeTime)
            {
                throw new ArgumentException("Opening time must be earlier than closing time.");
            }
        }

        return new PoiOpeningHour
        {
            DayOfWeek = dayOfWeek,
            OpenTime = openTime,
            CloseTime = closeTime,
            IsClosed = isClosed,
        };
    }

    internal void AttachTo(PointOfInterest pointOfInterest)
    {
        PointOfInterest = pointOfInterest ?? throw new ArgumentNullException(nameof(pointOfInterest));
    }
}