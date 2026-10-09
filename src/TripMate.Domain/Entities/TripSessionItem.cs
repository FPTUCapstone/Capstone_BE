namespace TripMate.Domain.Entities;

public sealed class TripSessionItem
{
    public const string PendingStatus = "Pending";
    public const string ReachedStatus = "Reached";
    public const string SkippedStatus = "Skipped";

    private TripSessionItem()
    {
    }

    public long SessionId { get; private set; }
    public TripSession Session { get; private set; } = null!;
    public long ItineraryItemId { get; private set; }
    public ItineraryItem ItineraryItem { get; private set; } = null!;
    public int SequenceNo { get; private set; }
    public long PoiId { get; private set; }
    public string PoiName { get; private set; } = string.Empty;
    public decimal Latitude { get; private set; }
    public decimal Longitude { get; private set; }
    public DateTimeOffset PlannedArrivalUtc { get; private set; }
    public DateTimeOffset PlannedDepartureUtc { get; private set; }
    public bool IsMandatory { get; private set; }
    public DateTimeOffset? ReachedAtUtc { get; private set; }
    public DateTimeOffset? SkippedAtUtc { get; private set; }

    public string Status => ReachedAtUtc.HasValue
        ? ReachedStatus
        : SkippedAtUtc.HasValue ? SkippedStatus : PendingStatus;

    public static TripSessionItem Snapshot(
        long itineraryItemId,
        int sequenceNo,
        long poiId,
        string poiName,
        decimal latitude,
        decimal longitude,
        DateTimeOffset plannedArrivalUtc,
        DateTimeOffset plannedDepartureUtc,
        bool isMandatory)
    {
        if (itineraryItemId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(itineraryItemId));
        }

        if (sequenceNo <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sequenceNo));
        }

        if (poiId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(poiId));
        }

        if (string.IsNullOrWhiteSpace(poiName))
        {
            throw new ArgumentException("A POI name is required.", nameof(poiName));
        }

        var normalizedName = poiName.Trim();
        if (normalizedName.Length > PointOfInterest.NameMaxLength)
        {
            throw new ArgumentException(
                $"A POI name cannot exceed {PointOfInterest.NameMaxLength} characters.",
                nameof(poiName));
        }

        if (latitude is < -90 or > 90)
        {
            throw new ArgumentOutOfRangeException(nameof(latitude));
        }

        if (longitude is < -180 or > 180)
        {
            throw new ArgumentOutOfRangeException(nameof(longitude));
        }

        if (plannedArrivalUtc.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("The planned arrival must be UTC.", nameof(plannedArrivalUtc));
        }

        if (plannedDepartureUtc.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("The planned departure must be UTC.", nameof(plannedDepartureUtc));
        }

        if (plannedDepartureUtc < plannedArrivalUtc)
        {
            throw new ArgumentException(
                "The planned departure cannot precede the planned arrival.",
                nameof(plannedDepartureUtc));
        }

        return new TripSessionItem
        {
            ItineraryItemId = itineraryItemId,
            SequenceNo = sequenceNo,
            PoiId = poiId,
            PoiName = normalizedName,
            Latitude = latitude,
            Longitude = longitude,
            PlannedArrivalUtc = plannedArrivalUtc,
            PlannedDepartureUtc = plannedDepartureUtc,
            IsMandatory = isMandatory,
        };
    }

    internal void AttachTo(TripSession session)
    {
        Session = session ?? throw new ArgumentNullException(nameof(session));
    }

    internal void MarkReached(DateTimeOffset reachedAtUtc)
    {
        if (!ReachedAtUtc.HasValue)
        {
            ReachedAtUtc = reachedAtUtc;
        }
    }

    internal void MarkSkipped(DateTimeOffset skippedAtUtc)
    {
        if (Status == PendingStatus)
        {
            SkippedAtUtc = skippedAtUtc;
        }
    }
}