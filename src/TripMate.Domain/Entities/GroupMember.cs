using TripMate.Domain.Enums;

namespace TripMate.Domain.Entities;

/**
 * [UC-17] Group Member Entity
 * Maps social.GroupMembers in database/tripmate_schema_v7.sql.
 * Represents a user's participation in a travel group with composite key (group_id, user_id).
 *
 * Properties:
 *   - GroupId (long): Target group ID.
 *   - UserId (long): Member user ID.
 *   - LocationSharingEnabled (bool): Toggle for real-time location sharing.
 *   - Status (GroupMemberStatus): Active, Removed, or Left.
 *   - JoinedAtUtc (DateTimeOffset): UTC timestamp when member joined.
 *   - LeftAtUtc (DateTimeOffset?): UTC timestamp when member left/removed.
 */
public class GroupMember
{
    public long GroupId { get; set; }

    public TravelGroup TravelGroup { get; set; } = null!;

    public long UserId { get; set; }

    public User User { get; set; } = null!;

    public bool LocationSharingEnabled { get; set; }

    public GroupMemberStatus Status { get; set; } = GroupMemberStatus.Active;

    public DateTimeOffset JoinedAtUtc { get; set; }

    public DateTimeOffset? LeftAtUtc { get; set; }
}