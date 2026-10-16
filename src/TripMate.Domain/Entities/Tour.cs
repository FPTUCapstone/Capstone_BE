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
    public const int TimeZoneIdMaxLength = 100;
    public const int TermsMaxLength = 4000;
    public const int MaxDestinations = 10;
    public const int DestinationSummaryMaxLength = 300;
    public const string DestinationCollation = "Vietnamese_100_CI_AS";

    private readonly List<TourSchedule> _schedules = [];
    private readonly List<TourDestination> _destinations = [];
    private readonly List<TourItineraryItem> _itineraryItems = [];
    private readonly List<TourCancellationPolicyRule> _cancellationPolicyRules = [];
    private readonly List<TourMedia> _media = [];
    private readonly List<TourMediaUploadOperation> _mediaUploadOperations = [];

    private Tour()
    {
    }

    public long OperatorUserId { get; private set; }

    public OperatorProfile OperatorProfile { get; private set; } = null!;

    public int? CategoryId { get; private set; }

    public TourCategory? Category { get; private set; }

    public string Title { get; private set; } = string.Empty;

    public string DestinationSummary { get; private set; } = string.Empty;

    public string? Description { get; private set; }

    public decimal BasePrice { get; private set; }

    public int DurationDays { get; private set; }

    public string? TimeZoneId { get; private set; }

    public string? Highlights { get; private set; }

    public string? Inclusions { get; private set; }

    public string? Exclusions { get; private set; }

    public string? TravellerRequirements { get; private set; }

    public long? CancellationPolicyTemplateId { get; private set; }

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

    public IReadOnlyCollection<TourItineraryItem> ItineraryItems => _itineraryItems.AsReadOnly();

    public IReadOnlyCollection<TourCancellationPolicyRule> CancellationPolicyRules =>
        _cancellationPolicyRules.AsReadOnly();

    public IReadOnlyCollection<TourMedia> Media => _media.AsReadOnly();

    public IReadOnlyCollection<TourMediaUploadOperation> MediaUploadOperations =>
        _mediaUploadOperations.AsReadOnly();

    public static Tour CreateDraft(long operatorUserId, int? categoryId, string title,
        string description, decimal basePrice, int durationDays,
        IEnumerable<long> destinationIds, string destinationSummary,
        CancellationPolicyTemplate cancellationPolicy,
        IEnumerable<TourItineraryItem> itineraryItems,
        IEnumerable<TourSchedule> schedules, DateTimeOffset nowUtc,
        string? inclusions = null, string? exclusions = null,
        string? highlights = null, string? travellerRequirements = null, string? timeZoneId = null)
    {
        if (operatorUserId <= 0) throw new ArgumentOutOfRangeException(nameof(operatorUserId));
        if (categoryId <= 0) throw new ArgumentOutOfRangeException(nameof(categoryId));
        if (string.IsNullOrWhiteSpace(title) || title.Trim().Length > TitleMaxLength)
            throw new ArgumentException("A valid title is required.", nameof(title));
        if (string.IsNullOrWhiteSpace(description))
            throw new ArgumentException("A description is required.", nameof(description));
        EnsureWholeVnd(basePrice, nameof(basePrice));
        if (durationDays <= 0) throw new ArgumentOutOfRangeException(nameof(durationDays));
        var items = itineraryItems?.ToArray() ?? throw new ArgumentNullException(nameof(itineraryItems));
        var departures = schedules?.ToArray() ?? throw new ArgumentNullException(nameof(schedules));
        if (items.Length == 0) throw new ArgumentException("At least one itinerary item is required.", nameof(itineraryItems));
        if (departures.Length == 0) throw new ArgumentException("At least one schedule is required.", nameof(schedules));
        if (items.GroupBy(item => (item.DayNo, item.SequenceNo)).Any(group => group.Count() > 1))
            throw new ArgumentException("Itinerary ordering must be unique.", nameof(itineraryItems));
        if (items.Any(item => item.DayNo > durationDays))
            throw new ArgumentException(
                "Each itinerary day must be within the tour duration.",
                nameof(itineraryItems));

        var tour = new Tour
        {
            OperatorUserId = operatorUserId,
            CategoryId = categoryId,
            Title = title.Trim(),
            Description = description.Trim(),
            BasePrice = decimal.Round(basePrice, 2, MidpointRounding.AwayFromZero),
            DurationDays = durationDays,
            Inclusions = NormalizeTerms(inclusions, nameof(inclusions)),
            Exclusions = NormalizeTerms(exclusions, nameof(exclusions)),
            Highlights = NormalizeTerms(highlights, nameof(highlights)),
            TravellerRequirements = NormalizeTerms(travellerRequirements, nameof(travellerRequirements)),
            TimeZoneId = NormalizeTimeZoneId(timeZoneId),
            Status = TourStatus.Draft,
            CreatedAtUtc = nowUtc.ToUniversalTime(),
            UpdatedAtUtc = nowUtc.ToUniversalTime(),
        };
        tour.ReplaceDraftDestinations(destinationIds, destinationSummary);
        tour.ApplyCancellationPolicySnapshot(cancellationPolicy, nowUtc);
        foreach (var item in items) { item.AttachTo(tour); tour._itineraryItems.Add(item); }
        foreach (var schedule in departures) { schedule.AttachTo(tour); tour._schedules.Add(schedule); }
        return tour;
    }

    public void SubmitForReview(DateTimeOffset submittedAtUtc)
    {
        if (Status is not (TourStatus.Draft or TourStatus.Rejected))
        {
            throw new InvalidOperationException("Only Draft or Rejected tours can be submitted for review.");
        }

        Status = TourStatus.Pending;
        RejectionReason = null;
        UpdatedAtUtc = submittedAtUtc.ToUniversalTime();
    }

    public void UpdateDraft(
        int? categoryId,
        string title,
        string description,
        decimal basePrice,
        int durationDays,
        string? inclusions,
        string? exclusions,
        DateTimeOffset updatedAtUtc,
        string? highlights = null,
        string? travellerRequirements = null,
        string? timeZoneId = null)
    {
        if (Status is not (TourStatus.Draft or TourStatus.Rejected))
        {
            throw new InvalidOperationException("Only Draft or Rejected tours can be updated.");
        }

        if (categoryId <= 0) throw new ArgumentOutOfRangeException(nameof(categoryId));
        if (string.IsNullOrWhiteSpace(title) || title.Trim().Length > TitleMaxLength)
            throw new ArgumentException("A valid title is required.", nameof(title));
        if (string.IsNullOrWhiteSpace(description))
            throw new ArgumentException("A description is required.", nameof(description));
        EnsureWholeVnd(basePrice, nameof(basePrice));
        if (durationDays <= 0) throw new ArgumentOutOfRangeException(nameof(durationDays));
        string? normalizedInclusions = NormalizeTerms(inclusions, nameof(inclusions));
        string? normalizedExclusions = NormalizeTerms(exclusions, nameof(exclusions));
        string? normalizedHighlights = NormalizeTerms(highlights, nameof(highlights));
        string? normalizedRequirements = NormalizeTerms(travellerRequirements, nameof(travellerRequirements));
        string normalizedTimeZone = NormalizeTimeZoneId(timeZoneId);

        CategoryId = categoryId;
        Title = title.Trim();
        Description = description.Trim();
        BasePrice = decimal.Round(basePrice, 2, MidpointRounding.AwayFromZero);
        DurationDays = durationDays;
        Inclusions = normalizedInclusions;
        Exclusions = normalizedExclusions;
        Highlights = normalizedHighlights;
        TravellerRequirements = normalizedRequirements;
        TimeZoneId = normalizedTimeZone;
        UpdatedAtUtc = updatedAtUtc.ToUniversalTime();
    }

    /// <summary>
    /// Replaces the ordered destination list. The order of <paramref name="destinationIds"/> is the
    /// display order (sequence_no 1..n). Callers that replace persisted rows must delete the old links
    /// and save before calling this, so SQL unique indexes never see old and new rows together.
    /// </summary>
    public void ReplaceDraftDestinations(IEnumerable<long> destinationIds, string destinationSummary)
    {
        if (Status is not (TourStatus.Draft or TourStatus.Rejected))
        {
            throw new InvalidOperationException("Only Draft or Rejected tours can be updated.");
        }

        long[] ids = destinationIds?.ToArray() ?? throw new ArgumentNullException(nameof(destinationIds));
        if (ids.Length == 0 || ids.Length > MaxDestinations)
            throw new ArgumentException(
                $"Between 1 and {MaxDestinations} destinations are required.", nameof(destinationIds));
        if (ids.Any(id => id <= 0) || ids.Distinct().Count() != ids.Length)
            throw new ArgumentException("Destinations must be unique valid ids.", nameof(destinationIds));

        if (string.IsNullOrWhiteSpace(destinationSummary) || destinationSummary.Trim().Length > DestinationSummaryMaxLength)
            throw new ArgumentException("A valid destination summary is required.", nameof(destinationSummary));

        DestinationSummary = destinationSummary.Trim();
        _destinations.Clear();
        for (int index = 0; index < ids.Length; index++)
        {
            _destinations.Add(TourDestination.Create(this, ids[index], index + 1));
        }
    }

    public void ReplaceDraftChildren(
        IEnumerable<TourItineraryItem> itineraryItems,
        IEnumerable<TourSchedule> schedules)
    {
        if (Status is not (TourStatus.Draft or TourStatus.Rejected))
        {
            throw new InvalidOperationException("Only Draft or Rejected tours can be updated.");
        }

        var items = itineraryItems?.ToArray() ?? throw new ArgumentNullException(nameof(itineraryItems));
        var departures = schedules?.ToArray() ?? throw new ArgumentNullException(nameof(schedules));
        if (items.Length == 0) throw new ArgumentException("At least one itinerary item is required.", nameof(itineraryItems));
        if (departures.Length == 0) throw new ArgumentException("At least one schedule is required.", nameof(schedules));
        if (items.Any(item => item.DayNo > DurationDays) ||
            items.GroupBy(item => (item.DayNo, item.SequenceNo)).Any(group => group.Count() > 1))
        {
            throw new ArgumentException("The itinerary ordering is invalid.", nameof(itineraryItems));
        }

        _itineraryItems.Clear();
        _schedules.Clear();
        foreach (TourItineraryItem item in items)
        {
            item.AttachTo(this);
            _itineraryItems.Add(item);
        }
        foreach (TourSchedule schedule in departures)
        {
            schedule.AttachTo(this);
            _schedules.Add(schedule);
        }
    }

    /// <summary>
    /// The only place a cancellation-policy snapshot is written: copies the template's ordered cutoff
    /// rules onto the tour so later template edits cannot change an existing offer.
    /// </summary>
    public void ApplyCancellationPolicySnapshot(CancellationPolicyTemplate template, DateTimeOffset updatedAtUtc)
    {
        if (Status is not (TourStatus.Draft or TourStatus.Rejected))
        {
            throw new InvalidOperationException("Only Draft or Rejected tours can be updated.");
        }

        ArgumentNullException.ThrowIfNull(template);
        if (!template.IsActive || template.Rules.Count == 0)
            throw new ArgumentException("An active cancellation policy with rules is required.", nameof(template));

        _cancellationPolicyRules.Clear();
        foreach (var rule in template.Rules)
        {
            _cancellationPolicyRules.Add(
                TourCancellationPolicyRule.Create(this, rule.DaysBeforeDeparture, rule.CancellationFeePercent));
        }

        CancellationPolicyTemplateId = template.Id;
        UpdatedAtUtc = updatedAtUtc.ToUniversalTime();
    }

    public void Approve(long reviewerUserId, DateTimeOffset reviewedAtUtc)
    {
        if (Status != TourStatus.Pending) throw new InvalidOperationException("Only Pending tours can be approved.");
        if (reviewerUserId <= 0) throw new ArgumentOutOfRangeException(nameof(reviewerUserId));
        Status = TourStatus.Approved;
        ReviewedBy = reviewerUserId;
        ReviewedAtUtc = reviewedAtUtc.ToUniversalTime();
        RejectionReason = null;
        UpdatedAtUtc = ReviewedAtUtc.Value;
    }

    public void Reject(long reviewerUserId, string reason, DateTimeOffset reviewedAtUtc)
    {
        if (Status != TourStatus.Pending) throw new InvalidOperationException("Only Pending tours can be rejected.");
        if (reviewerUserId <= 0) throw new ArgumentOutOfRangeException(nameof(reviewerUserId));
        if (string.IsNullOrWhiteSpace(reason) || reason.Trim().Length > RejectionReasonMaxLength)
            throw new ArgumentException("A valid rejection reason is required.", nameof(reason));
        Status = TourStatus.Rejected;
        ReviewedBy = reviewerUserId;
        ReviewedAtUtc = reviewedAtUtc.ToUniversalTime();
        RejectionReason = reason.Trim();
        UpdatedAtUtc = ReviewedAtUtc.Value;
    }

    public void Publish(long reviewerUserId, DateTimeOffset publishedAtUtc)
    {
        if (Status != TourStatus.Approved) throw new InvalidOperationException("Only Approved tours can be published.");
        if (reviewerUserId <= 0) throw new ArgumentOutOfRangeException(nameof(reviewerUserId));
        Status = TourStatus.Published;
        PublishedAtUtc = publishedAtUtc.ToUniversalTime();
        UpdatedAtUtc = PublishedAtUtc.Value;
    }

    private static void EnsureWholeVnd(decimal price, string parameterName)
    {
        if (price <= 0 || price != decimal.Truncate(price))
            throw new ArgumentOutOfRangeException(parameterName, "A positive whole VND amount is required.");
    }

    /// <summary>Time zone used when the operator does not choose one (Vietnam-only product).</summary>
    public const string DefaultTimeZoneId = "Asia/Ho_Chi_Minh";

    /// <summary>True when <paramref name="timeZoneId"/> is a time-zone identifier known to the host.</summary>
    public static bool IsKnownTimeZone(string? timeZoneId) =>
        !string.IsNullOrWhiteSpace(timeZoneId) &&
        timeZoneId.Trim().Length <= TimeZoneIdMaxLength &&
        TimeZoneInfo.TryFindSystemTimeZoneById(timeZoneId.Trim(), out _);

    private static string NormalizeTimeZoneId(string? timeZoneId)
    {
        if (string.IsNullOrWhiteSpace(timeZoneId)) return DefaultTimeZoneId;
        if (!IsKnownTimeZone(timeZoneId))
            throw new ArgumentException("A valid IANA time-zone identifier is required.", nameof(timeZoneId));
        return timeZoneId.Trim();
    }

    private static string? NormalizeTerms(string? value, string parameterName)
    {
        string? normalized = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        if (normalized?.Length > TermsMaxLength)
            throw new ArgumentException($"At most {TermsMaxLength} characters are allowed.", parameterName);
        return normalized;
    }
}