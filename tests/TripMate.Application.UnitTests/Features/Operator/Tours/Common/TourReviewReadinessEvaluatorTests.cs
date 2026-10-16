using FluentAssertions;

using TripMate.Application.Features.Operator.Tours.Common;
using TripMate.Domain.Enums;

namespace TripMate.Application.UnitTests.Features.Operator.Tours.Common;

public sealed class TourReviewReadinessEvaluatorTests
{
    [Fact]
    public void Evaluate_WhenEverythingIsPresent_HasNoBlockers()
    {
        TourReviewReadinessEvaluator.Evaluate(new TourReviewFacts(true, true, true, true)).Should().BeEmpty();
    }

    [Fact]
    public void Evaluate_WhenNothingIsPresent_ListsEveryBlockerWithStableCodes()
    {
        TourReviewReadinessEvaluator.Evaluate(new TourReviewFacts(false, false, false, false))
            .Should().Equal(
                "primary_image_missing",
                "future_departure_missing",
                "cancellation_policy_missing",
                "destination_missing");
    }

    [Theory]
    [InlineData(false, true, true, true, "primary_image_missing")]
    [InlineData(true, false, true, true, "future_departure_missing")]
    [InlineData(true, true, false, true, "cancellation_policy_missing")]
    [InlineData(true, true, true, false, "destination_missing")]
    public void Evaluate_ReportsOnlyTheMissingPrerequisite(
        bool image, bool departure, bool policy, bool destination, string expected)
    {
        TourReviewReadinessEvaluator.Evaluate(new TourReviewFacts(image, departure, policy, destination))
            .Should().Equal(expected);
    }

    [Theory]
    [InlineData(TourStatus.Draft, true)]
    [InlineData(TourStatus.Rejected, true)]
    [InlineData(TourStatus.Pending, false)]
    [InlineData(TourStatus.Approved, false)]
    [InlineData(TourStatus.Published, false)]
    [InlineData(TourStatus.Inactive, false)]
    public void IsEditable_OnlyDraftAndRejectedCanBeSubmitted(TourStatus status, bool expected)
    {
        TourReviewReadinessEvaluator.IsEditable(status).Should().Be(expected);
    }
}