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
    private GroupInvitation()
    {
    }

    private GroupInvitation(
        long groupId,
        long createdBy,
        string inviteCode,
        DateTimeOffset expiresAtUtc,
        int maxUses,
        DateTimeOffset createdAtUtc)
    {
        if (groupId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(groupId));
        }

        if (createdBy <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(createdBy));
        }

        if (string.IsNullOrWhiteSpace(inviteCode) || inviteCode.Length > 20)
        {
            throw new ArgumentException("An invitation code between 1 and 20 characters is required.", nameof(inviteCode));
        }

        if (expiresAtUtc <= createdAtUtc)
        {
            throw new ArgumentException("An invitation must expire after it is created.", nameof(expiresAtUtc));
        }

        if (maxUses <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxUses));
        }

        GroupId = groupId;
        CreatedBy = createdBy;
        InviteCode = inviteCode;
        ExpiresAtUtc = expiresAtUtc;
        MaxUses = maxUses;
        CreatedAtUtc = createdAtUtc;
    }

    public static GroupInvitation Create(
        long groupId,
        long createdBy,
        string inviteCode,
        DateTimeOffset expiresAtUtc,
        int maxUses,
        DateTimeOffset createdAtUtc) =>
        new(groupId, createdBy, inviteCode, expiresAtUtc, maxUses, createdAtUtc);

    public long GroupId { get; private set; }

    public TravelGroup TravelGroup { get; private set; } = null!;

    public string InviteCode { get; private set; } = string.Empty;

    public long CreatedBy { get; private set; }

    public User CreatorUser { get; private set; } = null!;

    public DateTimeOffset ExpiresAtUtc { get; private set; }

    public int MaxUses { get; private set; }

    public int UsedCount { get; private set; }

    public DateTimeOffset CreatedAtUtc { get; private set; }

    public bool IsUsableAt(DateTimeOffset now) =>
        ExpiresAtUtc > now && UsedCount < MaxUses;

    public void Expire(DateTimeOffset now)
    {
        if (now < CreatedAtUtc)
        {
            throw new ArgumentOutOfRangeException(nameof(now));
        }

        if (ExpiresAtUtc > now)
        {
            ExpiresAtUtc = now;
        }
    }
}