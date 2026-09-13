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
    private readonly List<GroupMember> _groupMembers = [];

    private TravelGroup()
    {
    }

    private TravelGroup(
        long itineraryId,
        long hostUserId,
        string name,
        DateTimeOffset createdAtUtc)
    {
        if (itineraryId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(itineraryId));
        }

        if (hostUserId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(hostUserId));
        }

        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("A travel group name is required.", nameof(name));
        }

        var normalizedName = name.Trim();
        if (normalizedName.Length > 150)
        {
            throw new ArgumentException("A travel group name cannot exceed 150 characters.", nameof(name));
        }

        ItineraryId = itineraryId;
        HostUserId = hostUserId;
        Name = normalizedName;
        CreatedAtUtc = createdAtUtc;
    }

    public static TravelGroup Create(
        long itineraryId,
        long hostUserId,
        string name,
        DateTimeOffset createdAtUtc) =>
        new(itineraryId, hostUserId, name, createdAtUtc);

    public long ItineraryId { get; private set; }

    public Itinerary Itinerary { get; private set; } = null!;

    public long HostUserId { get; private set; }

    public User HostUser { get; private set; } = null!;

    public string Name { get; private set; } = string.Empty;

    public DateTimeOffset CreatedAtUtc { get; private set; }

    public IReadOnlyCollection<GroupMember> GroupMembers => _groupMembers.AsReadOnly();

    public void AddMember(GroupMember member)
    {
        ArgumentNullException.ThrowIfNull(member);

        if (!ReferenceEquals(member.TravelGroup, this))
        {
            throw new ArgumentException("The member belongs to a different travel group.", nameof(member));
        }

        if (_groupMembers.Any(existing => existing.UserId == member.UserId))
        {
            throw new InvalidOperationException("A user cannot be added to the same travel group twice.");
        }

        _groupMembers.Add(member);
    }

}