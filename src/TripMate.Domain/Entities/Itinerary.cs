using TripMate.Domain.Common;

namespace TripMate.Domain.Entities;

/**
 * [UC-17] Itinerary Entity
 * Maps planning.Itineraries in database/tripmate_schema_v7.sql.
 * Represents an itinerary that can be linked to a TravelGroup.
 */
public class Itinerary : BaseEntity
{
    private Itinerary()
    {
    }

    private Itinerary(
        long travelerUserId,
        string? title,
        string sourceType,
        string status,
        DateTimeOffset createdAtUtc,
        DateTimeOffset updatedAtUtc)
    {
        if (travelerUserId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(travelerUserId));
        }

        if (string.IsNullOrWhiteSpace(sourceType))
        {
            throw new ArgumentException("An itinerary source type is required.", nameof(sourceType));
        }

        if (string.IsNullOrWhiteSpace(status))
        {
            throw new ArgumentException("An itinerary status is required.", nameof(status));
        }

        TravelerUserId = travelerUserId;
        Title = title?.Trim();
        SourceType = sourceType.Trim();
        Status = status.Trim();
        CreatedAtUtc = createdAtUtc;
        UpdatedAtUtc = updatedAtUtc;
    }

    public static Itinerary Create(
        long travelerUserId,
        string? title,
        string status,
        DateTimeOffset createdAtUtc,
        DateTimeOffset? updatedAtUtc = null,
        string sourceType = "Manual") =>
        new(travelerUserId, title, sourceType, status, createdAtUtc, updatedAtUtc ?? createdAtUtc);

    public long TravelerUserId { get; private set; }

    public User TravelerUser { get; private set; } = null!;

    public string SourceType { get; private set; } = "Manual";

    public string? Title { get; private set; }

    public string Status { get; private set; } = "Draft";

    public DateTimeOffset? ValidFromUtc { get; private set; }

    public DateTimeOffset? ValidToUtc { get; private set; }

    public DateTimeOffset CreatedAtUtc { get; private set; }

    public DateTimeOffset UpdatedAtUtc { get; private set; }

    private readonly List<TravelGroup> _travelGroups = [];

    public IReadOnlyCollection<TravelGroup> TravelGroups => _travelGroups.AsReadOnly();
}