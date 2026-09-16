namespace TripMate.Domain.Enums;

/**
 * [UC-17] Group Member Status Enum
 * Represents the membership status of a user within a travel group.
 * Matches CK constraint in database/tripmate_schema_v7.sql on social.GroupMembers(status):
 *   CHECK (status IN ('Active', 'Removed', 'Left'))
 */
public enum GroupMemberStatus
{
    Active = 1,
    Removed = 2,
    Left = 3
}