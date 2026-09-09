using TripMate.Domain.Common;

namespace TripMate.Domain.Entities;

/**
 * [UC-17] Group Invitation Entity
 * Maps social.GroupInvitations in database/tripmate_schema_v7.sql.
 * Represents an invitation code generated for sharing access to a travel group.
 *
 * Properties:
 *   - Id (long): Primary key (invitation_id).
 *   - GroupId (long): Target travel group ID.
 *   - InviteCode (string): Unique alphanumeric invitation code (max 20 chars).
 *   - CreatedBy (long): User ID of the creator.
 *   - ExpiresAtUtc (DateTimeOffset): Expiration UTC timestamp.
 *   - MaxUses (int): Maximum number of times the code can be used.
 *   - UsedCount (int): Current usage count.
 *   - CreatedAtUtc (DateTimeOffset): UTC timestamp of creation.
 */
public class GroupInvitation : BaseEntity
{
    public long GroupId { get; set; }

    public TravelGroup TravelGroup { get; set; } = null!;

    public string InviteCode { get; set; } = string.Empty;

    public long CreatedBy { get; set; }

    public User CreatorUser { get; set; } = null!;

    public DateTimeOffset ExpiresAtUtc { get; set; }

    public int MaxUses { get; set; } = 50;

    public int UsedCount { get; set; } = 0;

    public DateTimeOffset CreatedAtUtc { get; set; }
}