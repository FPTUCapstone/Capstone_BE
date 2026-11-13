using TripMate.Domain.Common;

namespace TripMate.Domain.Entities;

/// <summary>
/// A place the traveler said they have already been to when removing it from an itinerary (UC-10 traveler
/// feedback, D19). One row per traveler and place, holding the latest date. It is no like and no dislike: the
/// ranking only lets the place rest for a while.
/// </summary>
public sealed class PoiVisitedMark : BaseEntity
{
    private PoiVisitedMark()
    {
    }

    public long TravelerUserId { get; private set; }

    public long PointOfInterestId { get; private set; }

    public DateTimeOffset MarkedAtUtc { get; private set; }

    public static PoiVisitedMark Create(long travelerUserId, long poiId, DateTimeOffset markedAtUtc)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(travelerUserId);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(poiId);

        return new PoiVisitedMark
        {
            TravelerUserId = travelerUserId,
            PointOfInterestId = poiId,
            MarkedAtUtc = markedAtUtc.ToUniversalTime(),
        };
    }

    public void MarkAgain(DateTimeOffset markedAtUtc) => MarkedAtUtc = markedAtUtc.ToUniversalTime();
}