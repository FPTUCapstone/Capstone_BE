using FluentAssertions;

using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Application.UnitTests.Domain;

public sealed class TourPendingChangeSetTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 25, 0, 0, 0, TimeSpan.Zero);

    private static TourPendingChangeSet NewDraft(DateTimeOffset? createdAtUtc = null) =>
        TourPendingChangeSet.CreateDraft(
            tourId: 10,
            submittedByUserId: 20,
            categoryId: 3,
            title: "Da Nang Explorer",
            description: "A 3-day tour around Da Nang.",
            basePrice: 1_500_000m,
            durationDays: 3,
            inclusions: "Hotel",
            exclusions: null,
            highlights: "Beach",
            travellerRequirements: null,
            timeZoneId: Tour.DefaultTimeZoneId,
            destinationIdsJson: "[1,2]",
            destinationSummary: "Da Nang, Hoi An",
            cancellationPolicyTemplateId: 7,
            itineraryItemsJson: "[{\"dayNo\":1,\"sequenceNo\":1,\"poiId\":5}]",
            schedulesJson: "[{\"id\":null,\"startAtUtc\":\"2026-12-01T00:00:00Z\"}]",
            createdAtUtc: createdAtUtc ?? Now);

    [Fact]
    public void CreateDraft_SeedsStatusAndTimestampsFromTheSuppliedSnapshot()
    {
        var changeSet = NewDraft();

        changeSet.TourId.Should().Be(10);
        changeSet.SubmittedByUserId.Should().Be(20);
        changeSet.Status.Should().Be(TourPendingChangeStatus.Draft);
        changeSet.CategoryId.Should().Be(3);
        changeSet.Title.Should().Be("Da Nang Explorer");
        changeSet.DestinationIdsJson.Should().Be("[1,2]");
        changeSet.ItineraryItemsJson.Should().Be("[{\"dayNo\":1,\"sequenceNo\":1,\"poiId\":5}]");
        changeSet.SchedulesJson.Should().Be("[{\"id\":null,\"startAtUtc\":\"2026-12-01T00:00:00Z\"}]");
        changeSet.CreatedAtUtc.Should().Be(Now);
        changeSet.UpdatedAtUtc.Should().Be(Now);
        changeSet.SubmittedAtUtc.Should().BeNull();
        changeSet.ReviewedByUserId.Should().BeNull();
        changeSet.ReviewedAtUtc.Should().BeNull();
        changeSet.RejectionReason.Should().BeNull();
    }

    [Theory]
    [InlineData(TourPendingChangeStatus.Draft)]
    [InlineData(TourPendingChangeStatus.Rejected)]
    public void SaveDraft_SucceedsFromDraftOrRejected(TourPendingChangeStatus status)
    {
        var changeSet = NewDraft();
        if (status == TourPendingChangeStatus.Rejected)
        {
            changeSet.Submit(Now.AddHours(1));
            changeSet.Reject(99, "Missing required field.", Now.AddHours(2));
        }

        var savedAt = Now.AddDays(1);
        changeSet.SaveDraft(
            categoryId: 4,
            title: "Updated title",
            description: "Updated description.",
            basePrice: 1_600_000m,
            durationDays: 4,
            inclusions: null,
            exclusions: "No flights",
            highlights: null,
            travellerRequirements: "Passport required",
            timeZoneId: Tour.DefaultTimeZoneId,
            destinationIdsJson: "[1]",
            destinationSummary: "Da Nang",
            cancellationPolicyTemplateId: 7,
            itineraryItemsJson: "[{\"dayNo\":1,\"sequenceNo\":1,\"poiId\":9}]",
            schedulesJson: "[{\"id\":null,\"startAtUtc\":\"2027-01-01T00:00:00Z\"}]",
            updatedAtUtc: savedAt);

        changeSet.Title.Should().Be("Updated title");
        changeSet.DurationDays.Should().Be(4);
        changeSet.DestinationIdsJson.Should().Be("[1]");
        changeSet.UpdatedAtUtc.Should().Be(savedAt);
        if (status == TourPendingChangeStatus.Rejected)
        {
            changeSet.Status.Should().Be(TourPendingChangeStatus.Rejected, "saving a rejected draft keeps it Rejected until resubmitted");
        }
    }

    [Theory]
    [InlineData(TourPendingChangeStatus.PendingReview)]
    [InlineData(TourPendingChangeStatus.Applied)]
    public void SaveDraft_ThrowsWhenNotDraftOrRejected(TourPendingChangeStatus status)
    {
        var changeSet = NewDraft();
        changeSet.Submit(Now.AddHours(1));
        if (status == TourPendingChangeStatus.Applied)
        {
            changeSet.MarkApplied(99, Now.AddHours(2));
        }

        var action = () => changeSet.SaveDraft(
            categoryId: 4, title: "x", description: "y", basePrice: 1m, durationDays: 1,
            inclusions: null, exclusions: null, highlights: null, travellerRequirements: null,
            timeZoneId: Tour.DefaultTimeZoneId, destinationIdsJson: "[1]", destinationSummary: "Da Nang",
            cancellationPolicyTemplateId: 7, itineraryItemsJson: "[]", schedulesJson: "[]",
            updatedAtUtc: Now.AddDays(1));

        action.Should().Throw<InvalidOperationException>();
    }

    [Theory]
    [InlineData(TourPendingChangeStatus.Draft)]
    [InlineData(TourPendingChangeStatus.Rejected)]
    public void Submit_SucceedsFromDraftOrRejected_SetsPendingReviewAndClearsRejectionReason(
        TourPendingChangeStatus status)
    {
        var changeSet = NewDraft();
        if (status == TourPendingChangeStatus.Rejected)
        {
            changeSet.Submit(Now.AddHours(1));
            changeSet.Reject(99, "Fix the pricing.", Now.AddHours(2));
        }

        var submittedAt = Now.AddHours(3);
        changeSet.Submit(submittedAt);

        changeSet.Status.Should().Be(TourPendingChangeStatus.PendingReview);
        changeSet.SubmittedAtUtc.Should().Be(submittedAt);
        changeSet.RejectionReason.Should().BeNull();
    }

    [Fact]
    public void Submit_ThrowsWhenAlreadyPendingReviewOrApplied()
    {
        var changeSet = NewDraft();
        changeSet.Submit(Now.AddHours(1));

        var resubmit = () => changeSet.Submit(Now.AddHours(2));
        resubmit.Should().Throw<InvalidOperationException>();

        changeSet.MarkApplied(99, Now.AddHours(3));
        var submitAfterApplied = () => changeSet.Submit(Now.AddHours(4));
        submitAfterApplied.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void MarkApplied_SucceedsOnlyFromPendingReview_SetsReviewerAndTimestamp()
    {
        var changeSet = NewDraft();
        changeSet.Submit(Now.AddHours(1));

        var reviewedAt = Now.AddHours(2);
        changeSet.MarkApplied(42, reviewedAt);

        changeSet.Status.Should().Be(TourPendingChangeStatus.Applied);
        changeSet.ReviewedByUserId.Should().Be(42);
        changeSet.ReviewedAtUtc.Should().Be(reviewedAt);
    }

    [Theory]
    [InlineData(TourPendingChangeStatus.Draft)]
    [InlineData(TourPendingChangeStatus.Rejected)]
    [InlineData(TourPendingChangeStatus.Applied)]
    public void MarkApplied_ThrowsWhenNotPendingReview(TourPendingChangeStatus status)
    {
        var changeSet = NewDraft();
        if (status != TourPendingChangeStatus.Draft)
        {
            changeSet.Submit(Now.AddHours(1));
        }

        if (status == TourPendingChangeStatus.Rejected)
        {
            changeSet.Reject(99, "Needs more detail.", Now.AddHours(2));
        }
        else if (status == TourPendingChangeStatus.Applied)
        {
            changeSet.MarkApplied(99, Now.AddHours(2));
        }

        var action = () => changeSet.MarkApplied(1, Now.AddHours(3));

        action.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Reject_SucceedsOnlyFromPendingReview_SetsReasonReviewerAndTimestamp()
    {
        var changeSet = NewDraft();
        changeSet.Submit(Now.AddHours(1));

        var reviewedAt = Now.AddHours(2);
        changeSet.Reject(42, "  The pricing is inconsistent.  ", reviewedAt);

        changeSet.Status.Should().Be(TourPendingChangeStatus.Rejected);
        changeSet.RejectionReason.Should().Be("The pricing is inconsistent.");
        changeSet.ReviewedByUserId.Should().Be(42);
        changeSet.ReviewedAtUtc.Should().Be(reviewedAt);
    }

    [Theory]
    [InlineData(TourPendingChangeStatus.Draft)]
    [InlineData(TourPendingChangeStatus.Applied)]
    public void Reject_ThrowsWhenNotPendingReview(TourPendingChangeStatus status)
    {
        var changeSet = NewDraft();
        if (status == TourPendingChangeStatus.Applied)
        {
            changeSet.Submit(Now.AddHours(1));
            changeSet.MarkApplied(99, Now.AddHours(2));
        }

        var action = () => changeSet.Reject(1, "A valid reason.", Now.AddHours(3));

        action.Should().Throw<InvalidOperationException>();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Reject_ThrowsWhenReasonIsEmpty(string? reason)
    {
        var changeSet = NewDraft();
        changeSet.Submit(Now.AddHours(1));

        var action = () => changeSet.Reject(1, reason!, Now.AddHours(2));

        action.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Reject_ThrowsWhenReasonExceedsMaxLength()
    {
        var changeSet = NewDraft();
        changeSet.Submit(Now.AddHours(1));

        var action = () => changeSet.Reject(1, new string('x', Tour.RejectionReasonMaxLength + 1), Now.AddHours(2));

        action.Should().Throw<ArgumentException>();
    }
}