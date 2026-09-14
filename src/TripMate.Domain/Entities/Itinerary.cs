using TripMate.Domain.Common;

namespace TripMate.Domain.Entities;

/**
 * [UC-17] Itinerary Entity
 * Maps planning.Itineraries in database/tripmate_schema_v7.sql.
 * Represents an itinerary that can be linked to a TravelGroup.
 */
public class Itinerary : BaseEntity
{
    public const int TitleMaxLength = 200;
    public const string ManualSourceType = "Manual";
    public const string DraftStatus = "Draft";
    public const string ActiveStatus = "Active";
    public const string CompletedStatus = "Completed";
    public const string CancelledStatus = "Cancelled";

    private Itinerary()
    {
    }

    private Itinerary(
        long travelerUserId,
        string? title,
        string status,
        DateTimeOffset createdAtUtc,
        DateTimeOffset updatedAtUtc)
    {
        if (travelerUserId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(travelerUserId));
        }

        var normalizedTitle = title?.Trim();
        if (normalizedTitle?.Length > TitleMaxLength)
        {
            throw new ArgumentException(
                $"An itinerary title cannot exceed {TitleMaxLength} characters.",
                nameof(title));
        }

        var normalizedStatus = status?.Trim();
        if (normalizedStatus is not (DraftStatus or ActiveStatus or CompletedStatus or CancelledStatus))
        {
            throw new ArgumentException("An itinerary status is invalid.", nameof(status));
        }

        TravelerUserId = travelerUserId;
        Title = normalizedTitle;
        SourceType = ManualSourceType;
        Status = normalizedStatus;
        CreatedAtUtc = createdAtUtc;
        UpdatedAtUtc = updatedAtUtc;
    }

    public static Itinerary CreateManual(
        long travelerUserId,
        string? title,
        string status,
        DateTimeOffset createdAtUtc,
        DateTimeOffset? updatedAtUtc = null) =>
        new(travelerUserId, title, status, createdAtUtc, updatedAtUtc ?? createdAtUtc);

    public long TravelerUserId { get; private set; }

    public User TravelerUser { get; private set; } = null!;

    public string SourceType { get; private set; } = ManualSourceType;

    public string? Title { get; private set; }

    public string Status { get; private set; } = DraftStatus;

    public DateTimeOffset? ValidFromUtc { get; private set; }

    public DateTimeOffset? ValidToUtc { get; private set; }

    public DateTimeOffset CreatedAtUtc { get; private set; }

    public DateTimeOffset UpdatedAtUtc { get; private set; }

    private readonly List<TravelGroup> _travelGroups = [];

    public IReadOnlyCollection<TravelGroup> TravelGroups => _travelGroups.AsReadOnly();
}