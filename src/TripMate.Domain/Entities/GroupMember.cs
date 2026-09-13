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
    private GroupMember()
    {
    }

    private GroupMember(
        TravelGroup travelGroup,
        long userId,
        DateTimeOffset joinedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(travelGroup);

        if (userId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(userId));
        }

        if (travelGroup.HostUserId != userId)
        {
            throw new ArgumentException("The initial member must be the travel group host.", nameof(userId));
        }

        GroupId = travelGroup.Id;
        TravelGroup = travelGroup;
        UserId = userId;
        LocationSharingEnabled = false;
        Status = GroupMemberStatus.Active;
        JoinedAtUtc = joinedAtUtc;
    }

    public static GroupMember CreateHost(
        TravelGroup travelGroup,
        long userId,
        DateTimeOffset joinedAtUtc) =>
        new(travelGroup, userId, joinedAtUtc);

    public long GroupId { get; private set; }

    public TravelGroup TravelGroup { get; private set; } = null!;

    public long UserId { get; private set; }

    public User User { get; private set; } = null!;

    public bool LocationSharingEnabled { get; private set; }

    public GroupMemberStatus Status { get; private set; } = GroupMemberStatus.Active;

    public DateTimeOffset JoinedAtUtc { get; private set; }

    public DateTimeOffset? LeftAtUtc { get; private set; }
}