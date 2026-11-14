using TripMate.Domain.Common;

namespace TripMate.Domain.Entities;

/// <summary>
/// A place a traveler marked with the heart on its detail page (UC-10 traveler feedback, D12). One row per
/// traveler and place; turning the heart off deletes the row.
/// </summary>
public sealed class PoiFavorite : BaseEntity
{
    private PoiFavorite()
    {
    }

    public long TravelerUserId { get; private set; }

    public long PointOfInterestId { get; private set; }

    public DateTimeOffset CreatedAtUtc { get; private set; }

    public static PoiFavorite Create(long travelerUserId, long poiId, DateTimeOffset createdAtUtc)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(travelerUserId);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(poiId);

        return new PoiFavorite
        {
            TravelerUserId = travelerUserId,
            PointOfInterestId = poiId,
            CreatedAtUtc = createdAtUtc.ToUniversalTime(),
        };
    }
}