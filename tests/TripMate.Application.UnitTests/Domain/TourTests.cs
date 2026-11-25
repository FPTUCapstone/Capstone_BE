using FluentAssertions;

using TripMate.Application.UnitTests.TestUtilities;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Application.UnitTests.Domain;

public sealed class TourTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 15, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void CreateDraft_WhenItineraryDayExceedsDuration_RejectsAggregate()
    {
        var action = () => NewDraft(durationDays: 1, itineraryDay: 2);

        action.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void CreateDraft_PersistsOrderedDestinationsTermsAndPolicySnapshot()
    {
        var policy = TourAuthoringTestData.NewPolicy(7);

        var tour = NewDraft(
            destinationIds: [20, 10, 30],
            policy: policy,
            inclusions: "  Hotel and breakfast  ",
            exclusions: "   ");

        tour.Destinations.OrderBy(link => link.SequenceNo)
            .Select(link => (link.DestinationId, link.SequenceNo))
            .Should().Equal((20L, 1), (10L, 2), (30L, 3));
        tour.Destinations.Should().OnlyContain(link => ReferenceEquals(link.Tour, tour));
        tour.Inclusions.Should().Be("Hotel and breakfast");
        tour.Exclusions.Should().BeNull();
        tour.CancellationPolicyTemplateId.Should().Be(7);
        tour.CancellationPolicyRules.Select(rule => (rule.DaysBeforeDeparture, rule.CancellationFeePercent))
            .Should().BeEquivalentTo([(30, 0m), (15, 25m), (0, 100m)]);
        tour.CancellationPolicyRules.Should().OnlyContain(rule => ReferenceEquals(rule.Tour, tour));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void ReplaceDraftDestinations_RequiresASummary(string summary)
    {
        var tour = NewDraft();

        var action = () => tour.ReplaceDraftDestinations([1], summary);

        action.Should().Throw<ArgumentException>();
        tour.DestinationSummary.Should().Be("Da Nang");
    }

    [Fact]
    public void ReplaceDraftDestinations_RejectsAnOverlongSummary()
    {
        var tour = NewDraft();

        var action = () => tour.ReplaceDraftDestinations([1], new string('x', Tour.DestinationSummaryMaxLength + 1));

        action.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void CreateDraft_WithoutDestination_Throws()
    {
        var action = () => NewDraft(destinationIds: []);

        action.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void CreateDraft_WithDuplicateOrInvalidDestination_Throws()
    {
        ((Action)(() => NewDraft(destinationIds: [5, 5]))).Should().Throw<ArgumentException>();
        ((Action)(() => NewDraft(destinationIds: [0]))).Should().Throw<ArgumentException>();
    }

    [Fact]
    public void CreateDraft_WithMoreThanTheMaximumDestinations_Throws()
    {
        var tooMany = Enumerable.Range(1, Tour.MaxDestinations + 1).Select(value => (long)value).ToArray();

        var action = () => NewDraft(destinationIds: tooMany);

        action.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void CreateDraft_WithInactiveOrEmptyPolicy_Throws()
    {
        ((Action)(() => NewDraft(policy: TourAuthoringTestData.NewPolicy(active: false))))
            .Should().Throw<ArgumentException>();
        ((Action)(() => NewDraft(withoutPolicy: true))).Should().Throw<ArgumentNullException>();
    }

    [Theory]
    [InlineData("1000000.5")]
    [InlineData("1000000.01")]
    public void CreateDraft_WithFractionalBasePrice_Throws(string price)
    {
        var action = () => NewDraft(basePrice: decimal.Parse(price, System.Globalization.CultureInfo.InvariantCulture));

        action.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void CreateDraft_AcceptsWholeVndPriceWithTrailingZeros()
    {
        var tour = NewDraft(basePrice: 500000.00m);

        tour.BasePrice.Should().Be(500000m);
    }

    [Fact]
    public void CreateDraft_WithOverlongTerms_Throws()
    {
        var overlong = new string('x', Tour.TermsMaxLength + 1);

        ((Action)(() => NewDraft(inclusions: overlong))).Should().Throw<ArgumentException>();
        ((Action)(() => NewDraft(exclusions: overlong))).Should().Throw<ArgumentException>();
    }

    [Fact]
    public void ApplyCancellationPolicySnapshot_ReplacesTheCopiedRules()
    {
        var tour = NewDraft(policy: TourAuthoringTestData.NewPolicy(1));
        var replacement = CancellationPolicyTemplate.Create("Strict", null, Now,
            [CancellationPolicyTemplateRule.Create(45, 0), CancellationPolicyTemplateRule.Create(0, 100)]);
        typeof(CancellationPolicyTemplate).GetProperty(nameof(CancellationPolicyTemplate.Id))!
            .SetValue(replacement, 2L);

        tour.ApplyCancellationPolicySnapshot(replacement, Now.AddHours(1));

        tour.CancellationPolicyTemplateId.Should().Be(2);
        tour.CancellationPolicyRules.Select(rule => rule.DaysBeforeDeparture)
            .Should().BeEquivalentTo([45, 0]);
        tour.UpdatedAtUtc.Should().Be(Now.AddHours(1));
    }

    [Fact]
    public void ApplyCancellationPolicySnapshot_RejectsInactiveTemplateAndLockedTour()
    {
        var tour = NewDraft();

        ((Action)(() => tour.ApplyCancellationPolicySnapshot(
            TourAuthoringTestData.NewPolicy(3, active: false), Now))).Should().Throw<ArgumentException>();

        tour.SubmitForReview(Now);
        ((Action)(() => tour.ApplyCancellationPolicySnapshot(TourAuthoringTestData.NewPolicy(3), Now)))
            .Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void ReplaceDraftDestinations_ReplacesOrderAndRejectsLockedTour()
    {
        var tour = NewDraft(destinationIds: [1, 2]);

        tour.ReplaceDraftDestinations([2, 1, 3], "Hoi An, Da Nang, Hue");

        tour.Destinations.OrderBy(link => link.SequenceNo).Select(link => link.DestinationId)
            .Should().Equal(2L, 1L, 3L);
        tour.DestinationSummary.Should().Be("Hoi An, Da Nang, Hue");

        tour.SubmitForReview(Now);
        ((Action)(() => tour.ReplaceDraftDestinations([1], "Da Nang"))).Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void UpdateDraft_NormalizesTermsAndRejectsFractionalPrice()
    {
        var tour = NewDraft();

        tour.UpdateDraft(null, "New title", "New description", 750000m, 2, "  Meals ", null, Now.AddHours(1));

        tour.Inclusions.Should().Be("Meals");
        tour.Exclusions.Should().BeNull();
        ((Action)(() => tour.UpdateDraft(null, "New title", "New description", 750000.5m, 2, null, null, Now)))
            .Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Terms_AreNormalizedAndTheTimeZoneDefaultsOrIsValidated()
    {
        var tour = NewDraft();
        tour.TimeZoneId.Should().Be("Asia/Ho_Chi_Minh");
        tour.Highlights.Should().BeNull();

        tour.UpdateDraft(null, "T", "D", 100m, 1, null, null, Now,
            highlights: "  Cable car  ", travellerRequirements: "  Passport  ", timeZoneId: " Asia/Bangkok ");

        tour.Highlights.Should().Be("Cable car");
        tour.TravellerRequirements.Should().Be("Passport");
        tour.TimeZoneId.Should().Be("Asia/Bangkok");
        ((Action)(() => tour.UpdateDraft(null, "T", "D", 100m, 1, null, null, Now, timeZoneId: "Mars/Olympus")))
            .Should().Throw<ArgumentException>();
        ((Action)(() => tour.UpdateDraft(null, "T", "D", 100m, 1, null, null, Now,
            highlights: new string('x', Tour.TermsMaxLength + 1)))).Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Lifecycle_AllowsPendingApprovalAndPublication()
    {
        var tour = NewDraft();

        tour.SubmitForReview(DateTimeOffset.UtcNow);
        tour.Approve(2, DateTimeOffset.UtcNow);
        tour.Publish(2, DateTimeOffset.UtcNow);

        tour.Status.Should().Be(TourStatus.Published);
        tour.PublishedAtUtc.Should().NotBeNull();
    }

    [Fact]
    public void Unpublish_MakesPublishedTourInactiveAndKeepsPublicationAuditTimestamp()
    {
        var tour = NewDraft();
        tour.SubmitForReview(DateTimeOffset.UtcNow);
        tour.Approve(2, DateTimeOffset.UtcNow);
        tour.Publish(2, Now);
        var publishedAt = tour.PublishedAtUtc;

        tour.Unpublish(Now.AddHours(1));

        tour.Status.Should().Be(TourStatus.Inactive);
        tour.PublishedAtUtc.Should().Be(publishedAt);
    }

    [Fact]
    public void Reject_RequiresPendingAndReason()
    {
        var tour = NewDraft();

        Action rejectDraft = () => tour.Reject(2, "Missing price details", DateTimeOffset.UtcNow);
        rejectDraft.Should().Throw<InvalidOperationException>();
        tour.SubmitForReview(DateTimeOffset.UtcNow);
        tour.Reject(2, "Missing price details", DateTimeOffset.UtcNow);

        tour.Status.Should().Be(TourStatus.Rejected);
        tour.RejectionReason.Should().Be("Missing price details");

        tour.SubmitForReview(DateTimeOffset.UtcNow);
        tour.Status.Should().Be(TourStatus.Pending);
        tour.RejectionReason.Should().BeNull();
    }

    [Theory]
    [InlineData(TourStatus.Draft)]
    [InlineData(TourStatus.Pending)]
    [InlineData(TourStatus.Rejected)]
    public void ApplyPendingChange_ThrowsUnlessApprovedOrPublished(TourStatus status)
    {
        var tour = NewDraft();
        if (status == TourStatus.Pending)
        {
            tour.SubmitForReview(Now);
        }
        else if (status == TourStatus.Rejected)
        {
            tour.SubmitForReview(Now);
            tour.Reject(2, "Needs changes", Now);
        }

        var action = () => tour.ApplyPendingChange(
            categoryId: 1, title: "New title", description: "New description", basePrice: 200000m,
            durationDays: 2, inclusions: null, exclusions: null, highlights: null,
            travellerRequirements: null, timeZoneId: Tour.DefaultTimeZoneId,
            destinationIds: [1], destinationSummary: "Da Nang",
            cancellationPolicy: TourAuthoringTestData.NewPolicy(),
            itineraryItems: [TourItineraryItem.Create(1, 1, 5, null, 60, null)],
            schedules: [TourSchedule.Create(Now.AddDays(10), Now.AddDays(11), null, 10, Now, adultPrice: 200000m)],
            appliedAtUtc: Now);

        action.Should().Throw<InvalidOperationException>();
    }

    [Theory]
    [InlineData(TourStatus.Approved)]
    [InlineData(TourStatus.Published)]
    public void ApplyPendingChange_SucceedsAndLeavesStatusAndReviewFieldsUnchanged(TourStatus status)
    {
        var tour = NewDraft();
        tour.SubmitForReview(Now);
        tour.Approve(2, Now);
        if (status == TourStatus.Published)
        {
            tour.Publish(2, Now);
        }

        var reviewedAtBefore = tour.ReviewedAtUtc;
        var reviewedByBefore = tour.ReviewedBy;
        var publishedAtBefore = tour.PublishedAtUtc;
        var appliedAt = Now.AddDays(1);

        tour.ApplyPendingChange(
            categoryId: 1, title: "Updated title", description: "Updated description",
            basePrice: 300000m, durationDays: 3, inclusions: "Breakfast", exclusions: null,
            highlights: null, travellerRequirements: null, timeZoneId: Tour.DefaultTimeZoneId,
            destinationIds: [1, 2], destinationSummary: "Da Nang, Hoi An",
            cancellationPolicy: TourAuthoringTestData.NewPolicy(),
            itineraryItems: [TourItineraryItem.Create(1, 1, 5, null, 60, null)],
            schedules: [TourSchedule.Create(Now.AddDays(10), Now.AddDays(11), null, 15, Now, adultPrice: 300000m)],
            appliedAtUtc: appliedAt);

        tour.Title.Should().Be("Updated title");
        tour.BasePrice.Should().Be(300000m);
        tour.DurationDays.Should().Be(3);
        tour.Inclusions.Should().Be("Breakfast");
        tour.Destinations.Select(link => link.DestinationId).Should().BeEquivalentTo([1L, 2L]);
        tour.Schedules.Should().ContainSingle(schedule => schedule.TotalCapacity == 15);
        tour.Status.Should().Be(status);
        tour.ReviewedAtUtc.Should().Be(reviewedAtBefore);
        tour.ReviewedBy.Should().Be(reviewedByBefore);
        tour.PublishedAtUtc.Should().Be(publishedAtBefore);
        tour.UpdatedAtUtc.Should().Be(appliedAt);
    }

    [Fact]
    public void ApplyPendingChange_UpdatesAnExistingScheduleInPlace_PreservingIdAndReservedCapacity()
    {
        var tour = NewDraft();
        tour.SubmitForReview(Now);
        tour.Approve(2, Now);
        var existingSchedule = tour.Schedules.Single();
        existingSchedule.Id = 501;
        existingSchedule.ReserveCapacity(3);

        var proposedSchedule = TourSchedule.Create(
            Now.AddDays(20), Now.AddDays(21), "New meeting point", 20, Now, adultPrice: 400000m);
        proposedSchedule.Id = existingSchedule.Id;

        tour.ApplyPendingChange(
            categoryId: 1, title: "T", description: "D", basePrice: 400000m, durationDays: 1,
            inclusions: null, exclusions: null, highlights: null, travellerRequirements: null,
            timeZoneId: Tour.DefaultTimeZoneId, destinationIds: [1], destinationSummary: "Da Nang",
            cancellationPolicy: TourAuthoringTestData.NewPolicy(),
            itineraryItems: [TourItineraryItem.Create(1, 1, 5, null, 60, null)],
            schedules: [proposedSchedule], appliedAtUtc: Now.AddDays(1));

        var resultSchedule = tour.Schedules.Single();
        resultSchedule.Id.Should().Be(501);
        resultSchedule.ReservedCapacity.Should().Be(3, "the sold-slot counter must survive the update");
        resultSchedule.TotalCapacity.Should().Be(20);
        resultSchedule.MeetingPoint.Should().Be("New meeting point");
    }

    [Fact]
    public void ApplyPendingChange_DoesNotRemoveAScheduleWithSoldSlotsEvenIfOmittedFromThePayload()
    {
        var tour = NewDraft();
        tour.SubmitForReview(Now);
        tour.Approve(2, Now);
        var existingSchedule = tour.Schedules.Single();
        existingSchedule.Id = 501;
        existingSchedule.ReserveCapacity(2);

        var newSchedule = TourSchedule.Create(
            Now.AddDays(30), Now.AddDays(31), null, 10, Now, adultPrice: 500000m);

        tour.ApplyPendingChange(
            categoryId: 1, title: "T", description: "D", basePrice: 500000m, durationDays: 1,
            inclusions: null, exclusions: null, highlights: null, travellerRequirements: null,
            timeZoneId: Tour.DefaultTimeZoneId, destinationIds: [1], destinationSummary: "Da Nang",
            cancellationPolicy: TourAuthoringTestData.NewPolicy(),
            itineraryItems: [TourItineraryItem.Create(1, 1, 5, null, 60, null)],
            schedules: [newSchedule], appliedAtUtc: Now.AddDays(1));

        tour.Schedules.Should().Contain(schedule => schedule.Id == 501 && schedule.ReservedCapacity == 2,
            "a schedule with sold slots must never be removed by ApplyPendingChange");
        tour.Schedules.Should().HaveCount(2, "the old sold-out schedule stays and the new one is added");
    }

    [Fact]
    public void ApplyPendingChange_RemovesAnExistingScheduleWithNoSoldSlotsWhenOmittedFromThePayload()
    {
        var tour = NewDraft();
        tour.SubmitForReview(Now);
        tour.Approve(2, Now);
        var existingSchedule = tour.Schedules.Single();
        existingSchedule.Id = 501;

        var newSchedule = TourSchedule.Create(
            Now.AddDays(30), Now.AddDays(31), null, 10, Now, adultPrice: 500000m);

        tour.ApplyPendingChange(
            categoryId: 1, title: "T", description: "D", basePrice: 500000m, durationDays: 1,
            inclusions: null, exclusions: null, highlights: null, travellerRequirements: null,
            timeZoneId: Tour.DefaultTimeZoneId, destinationIds: [1], destinationSummary: "Da Nang",
            cancellationPolicy: TourAuthoringTestData.NewPolicy(),
            itineraryItems: [TourItineraryItem.Create(1, 1, 5, null, 60, null)],
            schedules: [newSchedule], appliedAtUtc: Now.AddDays(1));

        tour.Schedules.Should().NotContain(schedule => schedule.Id == 501);
        tour.Schedules.Should().ContainSingle(schedule => schedule.TotalCapacity == 10);
    }

    [Fact]
    public void ApplyPendingChange_AddsANewScheduleWithANewIdAlongsideExistingOnes()
    {
        var tour = NewDraft();
        tour.SubmitForReview(Now);
        tour.Approve(2, Now);
        var existingSchedule = tour.Schedules.Single();
        existingSchedule.Id = 501;
        existingSchedule.ReserveCapacity(1);

        var keepProposal = TourSchedule.Create(
            existingSchedule.StartAtUtc, existingSchedule.EndAtUtc, existingSchedule.MeetingPoint,
            existingSchedule.TotalCapacity, Now, adultPrice: existingSchedule.AdultPrice);
        keepProposal.Id = 501;
        var newProposal = TourSchedule.Create(
            Now.AddDays(40), Now.AddDays(41), null, 5, Now, adultPrice: 600000m);

        tour.ApplyPendingChange(
            categoryId: 1, title: "T", description: "D", basePrice: 600000m, durationDays: 1,
            inclusions: null, exclusions: null, highlights: null, travellerRequirements: null,
            timeZoneId: Tour.DefaultTimeZoneId, destinationIds: [1], destinationSummary: "Da Nang",
            cancellationPolicy: TourAuthoringTestData.NewPolicy(),
            itineraryItems: [TourItineraryItem.Create(1, 1, 5, null, 60, null)],
            schedules: [keepProposal, newProposal], appliedAtUtc: Now.AddDays(1));

        tour.Schedules.Should().HaveCount(2);
        tour.Schedules.Should().Contain(schedule => schedule.Id == 501 && schedule.ReservedCapacity == 1);
        tour.Schedules.Should().Contain(schedule => schedule.Id != 501 && schedule.TotalCapacity == 5);
    }

    private static Tour NewDraft(
        long[]? destinationIds = null,
        CancellationPolicyTemplate? policy = null,
        decimal basePrice = 100m,
        int durationDays = 1,
        int itineraryDay = 1,
        string? inclusions = null,
        string? exclusions = null,
        bool withoutPolicy = false)
    {
        var template = policy ?? TourAuthoringTestData.NewPolicy();
        return Tour.CreateDraft(
            operatorUserId: 1,
            categoryId: 1,
            title: "Ha Noi",
            description: "A valid description",
            basePrice: basePrice,
            durationDays: durationDays,
            destinationIds: destinationIds ?? [1],
            destinationSummary: "Da Nang",
            cancellationPolicy: withoutPolicy ? null! : template,
            itineraryItems: [TourItineraryItem.Create(itineraryDay, 1, 5, null, 60, null)],
            schedules:
            [
                TourSchedule.Create(Now.AddDays(5), Now.AddDays(6), null, 10, Now, adultPrice: 100m),
            ],
            nowUtc: Now,
            inclusions: inclusions,
            exclusions: exclusions);
    }
}