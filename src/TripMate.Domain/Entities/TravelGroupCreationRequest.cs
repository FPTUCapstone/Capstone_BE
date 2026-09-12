using TripMate.Domain.Common;

namespace TripMate.Domain.Entities;

/**
 * Stores the result of a create-group operation so retries return the original group.
 */
public class TravelGroupCreationRequest : BaseEntity
{
    public long TravelerUserId { get; set; }

    public Guid IdempotencyKey { get; set; }

    public long TravelGroupId { get; set; }

    public TravelGroup TravelGroup { get; set; } = null!;

    public DateTimeOffset CreatedAtUtc { get; set; }
}
