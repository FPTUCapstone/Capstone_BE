using TripMate.Domain.Common;

namespace TripMate.Domain.Entities;

/**
 * [UC-17] Travel Group Entity
 * Maps social.TravelGroups in database/tripmate_schema_v7.sql.
 * Represents a group formed around a shared itinerary with an assigned Host.
 *
 * Properties:
 *   - Id (long): Primary key (group_id).
 *   - ItineraryId (long): Linked itinerary ID (itinerary_id).
 *   - HostUserId (long): ID of the creator/host traveler (host_user_id).
 *   - Name (string?): User-defined group name (max 150 chars).
 *   - CreatedAtUtc (DateTimeOffset): UTC timestamp of creation.
 */
public class TravelGroup : BaseEntity
{
    public long ItineraryId { get; set; }

    public Itinerary Itinerary { get; set; } = null!;

    public long HostUserId { get; set; }

    public User HostUser { get; set; } = null!;

    public string? Name { get; set; }

    public DateTimeOffset CreatedAtUtc { get; set; }

    public ICollection<GroupMember> GroupMembers { get; set; } = new List<GroupMember>();

    public ICollection<GroupInvitation> GroupInvitations { get; set; } = new List<GroupInvitation>();
}