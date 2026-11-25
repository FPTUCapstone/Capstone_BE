using TripMate.Domain.Common;
using TripMate.Domain.Enums;

namespace TripMate.Domain.Entities;

/// <summary>
/// A draft version of an Approved/Published tour (UC-36 Alternative Flow, TM-82). The live
/// <see cref="Tour"/> and its children stay untouched while this row exists; only
/// <see cref="Tour.ApplyPendingChange"/> (called on approval) mutates the live tour. No EF
/// dependency (AGENTS.md §2.1) and no Cancel/Discard transition — discard deletes the row outright
/// while Draft or Rejected.
/// </summary>
public class TourPendingChangeSet : BaseEntity
{
    private TourPendingChangeSet()
    {
    }

    public long TourId { get; private set; }

    public Tour Tour { get; private set; } = null!;

    public long SubmittedByUserId { get; private set; }

    public TourPendingChangeStatus Status { get; private set; }

    public int CategoryId { get; private set; }

    public string Title { get; private set; } = string.Empty;

    public string Description { get; private set; } = string.Empty;

    public decimal BasePrice { get; private set; }

    public int DurationDays { get; private set; }

    public string? Inclusions { get; private set; }

    public string? Exclusions { get; private set; }

    public string? Highlights { get; private set; }

    public string? TravellerRequirements { get; private set; }

    public string TimeZoneId { get; private set; } = Tour.DefaultTimeZoneId;

    public string DestinationSummary { get; private set; } = string.Empty;

    public long CancellationPolicyTemplateId { get; private set; }

    /// <summary>
    /// JSON array of proposed destination ids, ordered. See <see cref="SchedulesJson"/> for the
    /// id-matching contract the save/submit/approve handlers rely on.
    /// </summary>
    public string DestinationIdsJson { get; private set; } = "[]";

    public string ItineraryItemsJson { get; private set; } = "[]";

    /// <summary>
    /// JSON array of proposed schedules. Each entry carries the existing <c>TourSchedule.Id</c> it
    /// replaces, or a null id for a newly proposed schedule (spec §4.1). A past-departure schedule
    /// is always present with its original id and original values (spec §3 point D) — never
    /// re-validated against BR-22 and never eligible for removal.
    /// </summary>
    public string SchedulesJson { get; private set; } = "[]";

    public DateTimeOffset CreatedAtUtc { get; private set; }

    public DateTimeOffset UpdatedAtUtc { get; private set; }

    public DateTimeOffset? SubmittedAtUtc { get; private set; }

    public long? ReviewedByUserId { get; private set; }

    public DateTimeOffset? ReviewedAtUtc { get; private set; }

    public string? RejectionReason { get; private set; }

    public static TourPendingChangeSet CreateDraft(
        long tourId,
        long submittedByUserId,
        int categoryId,
        string title,
        string description,
        decimal basePrice,
        int durationDays,
        string? inclusions,
        string? exclusions,
        string? highlights,
        string? travellerRequirements,
        string timeZoneId,
        string destinationIdsJson,
        string destinationSummary,
        long cancellationPolicyTemplateId,
        string itineraryItemsJson,
        string schedulesJson,
        DateTimeOffset createdAtUtc)
    {
        var normalizedCreatedAtUtc = createdAtUtc.ToUniversalTime();
        var changeSet = new TourPendingChangeSet
        {
            TourId = tourId,
            SubmittedByUserId = submittedByUserId,
            Status = TourPendingChangeStatus.Draft,
            CreatedAtUtc = normalizedCreatedAtUtc,
            UpdatedAtUtc = normalizedCreatedAtUtc,
        };
        changeSet.SetProposedFields(categoryId, title, description, basePrice, durationDays,
            inclusions, exclusions, highlights, travellerRequirements, timeZoneId,
            destinationIdsJson, destinationSummary, cancellationPolicyTemplateId,
            itineraryItemsJson, schedulesJson);
        return changeSet;
    }

    public void SaveDraft(
        int categoryId,
        string title,
        string description,
        decimal basePrice,
        int durationDays,
        string? inclusions,
        string? exclusions,
        string? highlights,
        string? travellerRequirements,
        string timeZoneId,
        string destinationIdsJson,
        string destinationSummary,
        long cancellationPolicyTemplateId,
        string itineraryItemsJson,
        string schedulesJson,
        DateTimeOffset updatedAtUtc)
    {
        if (Status is not (TourPendingChangeStatus.Draft or TourPendingChangeStatus.Rejected))
            throw new InvalidOperationException("Only a Draft or Rejected version can be edited.");

        SetProposedFields(categoryId, title, description, basePrice, durationDays, inclusions,
            exclusions, highlights, travellerRequirements, timeZoneId, destinationIdsJson,
            destinationSummary, cancellationPolicyTemplateId, itineraryItemsJson, schedulesJson);
        UpdatedAtUtc = updatedAtUtc.ToUniversalTime();
    }

    public void Submit(DateTimeOffset submittedAtUtc)
    {
        if (Status is not (TourPendingChangeStatus.Draft or TourPendingChangeStatus.Rejected))
            throw new InvalidOperationException("Only a Draft or Rejected version can be submitted.");

        Status = TourPendingChangeStatus.PendingReview;
        SubmittedAtUtc = submittedAtUtc.ToUniversalTime();
        RejectionReason = null;
    }

    public void MarkApplied(long reviewerUserId, DateTimeOffset reviewedAtUtc)
    {
        if (Status != TourPendingChangeStatus.PendingReview)
            throw new InvalidOperationException("Only a PendingReview version can be approved.");

        Status = TourPendingChangeStatus.Applied;
        ReviewedByUserId = reviewerUserId;
        ReviewedAtUtc = reviewedAtUtc.ToUniversalTime();
    }

    public void Reject(long reviewerUserId, string reason, DateTimeOffset reviewedAtUtc)
    {
        if (Status != TourPendingChangeStatus.PendingReview)
            throw new InvalidOperationException("Only a PendingReview version can be rejected.");
        if (string.IsNullOrWhiteSpace(reason) || reason.Trim().Length > Tour.RejectionReasonMaxLength)
            throw new ArgumentException("A valid rejection reason is required.", nameof(reason));

        Status = TourPendingChangeStatus.Rejected;
        RejectionReason = reason.Trim();
        ReviewedByUserId = reviewerUserId;
        ReviewedAtUtc = reviewedAtUtc.ToUniversalTime();
    }

    private void SetProposedFields(
        int categoryId,
        string title,
        string description,
        decimal basePrice,
        int durationDays,
        string? inclusions,
        string? exclusions,
        string? highlights,
        string? travellerRequirements,
        string timeZoneId,
        string destinationIdsJson,
        string destinationSummary,
        long cancellationPolicyTemplateId,
        string itineraryItemsJson,
        string schedulesJson)
    {
        CategoryId = categoryId;
        Title = title;
        Description = description;
        BasePrice = basePrice;
        DurationDays = durationDays;
        Inclusions = inclusions;
        Exclusions = exclusions;
        Highlights = highlights;
        TravellerRequirements = travellerRequirements;
        TimeZoneId = timeZoneId;
        DestinationIdsJson = destinationIdsJson;
        DestinationSummary = destinationSummary;
        CancellationPolicyTemplateId = cancellationPolicyTemplateId;
        ItineraryItemsJson = itineraryItemsJson;
        SchedulesJson = schedulesJson;
    }
}
