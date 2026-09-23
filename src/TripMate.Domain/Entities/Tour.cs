using TripMate.Domain.Common;
using TripMate.Domain.Enums;

namespace TripMate.Domain.Entities;

/// <summary>
/// Read model for a reusable tour template stored in commerce.Tours.
/// Tour creation and publication transitions belong to their owning use cases.
/// </summary>
public class Tour : BaseEntity
{
    public const int TitleMaxLength = 200;
    public const int RejectionReasonMaxLength = 500;
    public const string DestinationCollation = "Vietnamese_100_CI_AS";

    private readonly List<TourSchedule> _schedules = [];
    private readonly List<TourDestination> _destinations = [];

    private Tour()
    {
    }

    public long OperatorUserId { get; private set; }

    public OperatorProfile OperatorProfile { get; private set; } = null!;

    public string Title { get; private set; } = string.Empty;

    public string? Description { get; private set; }

    public decimal BasePrice { get; private set; }

    public int DurationDays { get; private set; }

    public TourStatus Status { get; private set; }

    public string? RejectionReason { get; private set; }

    public long? ReviewedBy { get; private set; }

    public User? Reviewer { get; private set; }

    public DateTimeOffset? ReviewedAtUtc { get; private set; }

    public DateTimeOffset? PublishedAtUtc { get; private set; }

    public DateTimeOffset CreatedAtUtc { get; private set; }

    public DateTimeOffset UpdatedAtUtc { get; private set; }

    public IReadOnlyCollection<TourSchedule> Schedules => _schedules.AsReadOnly();

    public IReadOnlyCollection<TourDestination> Destinations => _destinations.AsReadOnly();
}