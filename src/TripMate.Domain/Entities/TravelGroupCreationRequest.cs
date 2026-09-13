using TripMate.Domain.Common;

namespace TripMate.Domain.Entities;

/**
 * Stores the result of a create-group operation so retries return the original group.
 */
public class TravelGroupCreationRequest : BaseEntity
{
    private TravelGroupCreationRequest()
    {
    }

    private TravelGroupCreationRequest(
        long travelerUserId,
        Guid idempotencyKey,
        long itineraryId,
        string groupName,
        TravelGroup travelGroup,
        DateTimeOffset createdAtUtc)
    {
        if (travelerUserId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(travelerUserId));
        }

        if (idempotencyKey == Guid.Empty)
        {
            throw new ArgumentException("An idempotency key is required.", nameof(idempotencyKey));
        }

        if (itineraryId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(itineraryId));
        }

        if (string.IsNullOrWhiteSpace(groupName))
        {
            throw new ArgumentException("A group name is required.", nameof(groupName));
        }

        ArgumentNullException.ThrowIfNull(travelGroup);

        var normalizedGroupName = groupName.Trim();
        if (normalizedGroupName.Length > 150)
        {
            throw new ArgumentException("A group name cannot exceed 150 characters.", nameof(groupName));
        }

        if (travelGroup.ItineraryId != itineraryId
            || travelGroup.HostUserId != travelerUserId
            || !string.Equals(travelGroup.Name, normalizedGroupName, StringComparison.Ordinal))
        {
            throw new ArgumentException("The creation request must match the travel group aggregate.", nameof(travelGroup));
        }

        TravelerUserId = travelerUserId;
        IdempotencyKey = idempotencyKey;
        ItineraryId = itineraryId;
        GroupName = normalizedGroupName;
        TravelGroupId = travelGroup.Id;
        TravelGroup = travelGroup;
        CreatedAtUtc = createdAtUtc;
    }

    public static TravelGroupCreationRequest Create(
        long travelerUserId,
        Guid idempotencyKey,
        long itineraryId,
        string groupName,
        TravelGroup travelGroup,
        DateTimeOffset createdAtUtc) =>
        new(travelerUserId, idempotencyKey, itineraryId, groupName, travelGroup, createdAtUtc);

    public long TravelerUserId { get; private set; }

    public Guid IdempotencyKey { get; private set; }

    public long ItineraryId { get; private set; }

    public string GroupName { get; private set; } = string.Empty;

    public long TravelGroupId { get; private set; }

    public TravelGroup TravelGroup { get; private set; } = null!;

    public DateTimeOffset CreatedAtUtc { get; private set; }
}