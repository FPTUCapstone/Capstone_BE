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
