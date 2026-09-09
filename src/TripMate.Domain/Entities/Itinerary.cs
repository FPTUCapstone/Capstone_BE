using TripMate.Domain.Common;

namespace TripMate.Domain.Entities;

/**
 * [UC-17] Itinerary Entity
 * Maps planning.Itineraries in database/tripmate_schema_v7.sql.
 * Represents an itinerary that can be linked to a TravelGroup.
 */
public class Itinerary : BaseEntity
{
    public long TravelerUserId { get; set; }

    public User TravelerUser { get; set; } = null!;

    public string SourceType { get; set; } = "Manual";

    public string? Title { get; set; }

    public string Status { get; set; } = "Draft";

    public DateTimeOffset? ValidFromUtc { get; set; }

    public DateTimeOffset? ValidToUtc { get; set; }

    public DateTimeOffset CreatedAtUtc { get; set; }

    public DateTimeOffset UpdatedAtUtc { get; set; }

    public ICollection<TravelGroup> TravelGroups { get; set; } = new List<TravelGroup>();
}