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
    public const string CspGeneratedSourceType = "CSPGenerated";
    public const string BookedTourSourceType = "BookedTour";
    public const string DraftStatus = "Draft";
    public const string ActiveStatus = "Active";
    public const string CompletedStatus = "Completed";
    public const string CancelledStatus = "Cancelled";
    public const int SolverOutcomeMaxLength = 64;
    public const int ObjectiveModeMaxLength = 20;

    private Itinerary()
    {
    }

    private Itinerary(
        long travelerUserId,
        string? title,
        string status,
        DateTimeOffset createdAtUtc,
        DateTimeOffset updatedAtUtc,
        int version)
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

        if (version <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(version));
        }

        TravelerUserId = travelerUserId;
        Title = normalizedTitle;
        SourceType = ManualSourceType;
        Status = normalizedStatus;
        CreatedAtUtc = createdAtUtc;
        UpdatedAtUtc = updatedAtUtc;
        Version = version;
    }

    public static Itinerary CreateManual(
        long travelerUserId,
        string? title,
        string status,
        DateTimeOffset createdAtUtc,
        DateTimeOffset? updatedAtUtc = null) =>
        new(travelerUserId, title, status, createdAtUtc, updatedAtUtc ?? createdAtUtc, version: 1);

    public static Itinerary CreateCspGenerated(
        SchedulingRequest schedulingRequest,
        string title,
        DateTimeOffset validFromUtc,
        DateTimeOffset validToUtc,
        int version = 1)
    {
        ArgumentNullException.ThrowIfNull(schedulingRequest);

        if (validToUtc <= validFromUtc)
        {
            throw new ArgumentException(
                "A generated itinerary must end after it starts.",
                nameof(validToUtc));
        }

        var itinerary = new Itinerary(
            schedulingRequest.TravelerUserId,
            title,
            DraftStatus,
            schedulingRequest.RequestedAtUtc,
            schedulingRequest.RequestedAtUtc,
            version)
        {
            SourceType = CspGeneratedSourceType,
            SchedulingRequest = schedulingRequest,
            ValidFromUtc = validFromUtc,
            ValidToUtc = validToUtc,
        };

        return itinerary;
    }

    public static Itinerary CreateBookedTour(
        long travelerUserId,
        string? title,
        long sourceBookingId,
        long sourceTourId,
        DateTimeOffset createdAtUtc,
        DateTimeOffset? validFromUtc = null,
        DateTimeOffset? validToUtc = null)
    {
        if (sourceBookingId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sourceBookingId));
        }

        if (sourceTourId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sourceTourId));
        }

        return new Itinerary(
            travelerUserId,
            title,
            ActiveStatus,
            createdAtUtc,
            createdAtUtc,
            version: 1)
        {
            SourceType = BookedTourSourceType,
            SourceBookingId = sourceBookingId,
            SourceTourId = sourceTourId,
            ValidFromUtc = validFromUtc,
            ValidToUtc = validToUtc,
        };
    }

    public long TravelerUserId { get; private set; }

    public User TravelerUser { get; private set; } = null!;

    public string SourceType { get; private set; } = ManualSourceType;

    public string? Title { get; private set; }

    public string Status { get; private set; } = DraftStatus;

    public long? SchedulingRequestId { get; private set; }

    public SchedulingRequest? SchedulingRequest { get; private set; }

    public long? SourceTourId { get; private set; }

    public Tour? SourceTour { get; private set; }

    public long? SourceBookingId { get; private set; }

    public Booking? SourceBooking { get; private set; }

    public DateTimeOffset? ValidFromUtc { get; private set; }

    public DateTimeOffset? ValidToUtc { get; private set; }

    public DateTimeOffset CreatedAtUtc { get; private set; }

    public DateTimeOffset UpdatedAtUtc { get; private set; }

    public int Version { get; private set; } = 1;

    /// <summary>
    /// How the schedule was produced (ADR-002): the solver outcome, or <c>manual_order</c> for a traveler's own order.
    /// Null for itineraries saved before provenance was recorded.
    /// </summary>
    public string? SolverOutcome { get; private set; }

    /// <summary>CSP objective mode used for the schedule; null when no CSP objective was involved.</summary>
    public string? ObjectiveMode { get; private set; }

    /// <summary>True when the CSP explored its whole search tree; null when the CSP did not run.</summary>
    public bool? SearchCompleted { get; private set; }

    /// <summary>True when the time-of-day step reordered the solver's schedule.</summary>
    public bool? TimeOfDayRefined { get; private set; }

    public void RecordGenerationProvenance(
        string solverOutcome,
        string? objectiveMode,
        bool? searchCompleted,
        bool timeOfDayRefined)
    {
        if (string.IsNullOrWhiteSpace(solverOutcome))
        {
            throw new ArgumentException("A solver outcome is required.", nameof(solverOutcome));
        }

        var outcome = solverOutcome.Trim();
        if (outcome.Length > SolverOutcomeMaxLength)
        {
            throw new ArgumentException(
                $"Solver outcome cannot exceed {SolverOutcomeMaxLength} characters.", nameof(solverOutcome));
        }

        var mode = string.IsNullOrWhiteSpace(objectiveMode) ? null : objectiveMode.Trim();
        if (mode?.Length > ObjectiveModeMaxLength)
        {
            throw new ArgumentException(
                $"Objective mode cannot exceed {ObjectiveModeMaxLength} characters.", nameof(objectiveMode));
        }

        SolverOutcome = outcome;
        ObjectiveMode = mode;
        SearchCompleted = searchCompleted;
        TimeOfDayRefined = timeOfDayRefined;
    }

    public void Accept(DateTimeOffset acceptedAtUtc)
    {
        if (Status == ActiveStatus)
        {
            return;
        }

        if (Status != DraftStatus)
        {
            throw new InvalidOperationException("Only a draft itinerary can be accepted.");
        }

        Status = ActiveStatus;
        UpdatedAtUtc = acceptedAtUtc.ToUniversalTime();
    }

    private readonly List<TravelGroup> _travelGroups = [];

    private readonly List<ItineraryItem> _items = [];

    public IReadOnlyCollection<TravelGroup> TravelGroups => _travelGroups.AsReadOnly();

    public IReadOnlyCollection<ItineraryItem> Items => _items.AsReadOnly();

    public void AddItem(ItineraryItem item)
    {
        ArgumentNullException.ThrowIfNull(item);

        if (_items.Any(existing => existing.SequenceNo == item.SequenceNo))
        {
            throw new InvalidOperationException("An itinerary item sequence number must be unique.");
        }

        item.AttachTo(this);
        _items.Add(item);
    }
}