using FluentAssertions;

using TripMate.Domain.Entities;

namespace TripMate.Application.UnitTests.Domain;

public sealed class TourRecommendationSnapshotTests
{
    private static readonly DateTimeOffset RequestedAt =
        new(2026, 11, 4, 18, 30, 0, TimeSpan.FromHours(7));

    [Fact]
    public void Create_NormalizesCriteriaAndTimestamp()
    {
        var snapshot = TourSearchRequest.Create(
            travelerUserId: 42,
            destinationText: "  Da Nang  ",
            startDate: new DateOnly(2026, 12, 1),
            endDate: new DateOnly(2026, 12, 5),
            minBudget: 1_000_000m,
            maxBudget: 2_000_000m,
            interestTagsJson: "[\"beach\"]",
            requestedAtUtc: RequestedAt);

        snapshot.TravelerUserId.Should().Be(42);
        snapshot.DestinationText.Should().Be("Da Nang");
        snapshot.RequestedAtUtc.Should().Be(RequestedAt.ToUniversalTime());
        snapshot.Recommendations.Should().BeEmpty();
    }

    [Fact]
    public void Create_RejectsInvalidTravelerDateAndBudgetRanges()
    {
        Action invalidTraveler = () => CreateSnapshot(travelerUserId: 0);
        Action invalidDates = () => CreateSnapshot(
            startDate: new DateOnly(2026, 12, 2),
            endDate: new DateOnly(2026, 12, 1));
        Action invalidBudget = () => CreateSnapshot(minBudget: 2m, maxBudget: 1m);

        invalidTraveler.Should().Throw<ArgumentOutOfRangeException>();
        invalidDates.Should().Throw<ArgumentException>();
        invalidBudget.Should().Throw<ArgumentException>();
    }

    [Theory]
    [InlineData("80.00", false)]
    [InlineData("80.01", true)]
    public void AddRecommendation_DerivesStrictThresholdAndAssignsNavigation(
        string scoreText,
        bool expected)
    {
        var snapshot = CreateSnapshot();
        decimal score = decimal.Parse(scoreText, System.Globalization.CultureInfo.InvariantCulture);

        var log = snapshot.AddRecommendation(100, score, 80m, RequestedAt.AddMinutes(1));

        log.IsRecommended.Should().Be(expected);
        log.SearchRequest.Should().BeSameAs(snapshot);
        log.TourId.Should().Be(100);
        log.RecommendedAtUtc.Should().Be(RequestedAt.AddMinutes(1).ToUniversalTime());
        snapshot.Recommendations.Should().ContainSingle().Which.Should().BeSameAs(log);
    }

    [Theory]
    [InlineData("-0.01")]
    [InlineData("100.01")]
    public void AddRecommendation_RejectsScoreOutsideDatabaseRange(string scoreText)
    {
        var snapshot = CreateSnapshot();
        decimal score = decimal.Parse(scoreText, System.Globalization.CultureInfo.InvariantCulture);

        Action action = () => snapshot.AddRecommendation(100, score, 80m, RequestedAt);

        action.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void AddRecommendation_FloorsThresholdAtEighty()
    {
        var snapshot = CreateSnapshot();

        var log = snapshot.AddRecommendation(100, 80m, 50m, RequestedAt);

        log.IsRecommended.Should().BeFalse();
    }

    private static TourSearchRequest CreateSnapshot(
        long travelerUserId = 42,
        DateOnly? startDate = null,
        DateOnly? endDate = null,
        decimal? minBudget = null,
        decimal? maxBudget = null) =>
        TourSearchRequest.Create(
            travelerUserId,
            null,
            startDate,
            endDate,
            minBudget,
            maxBudget,
            "[\"beach\"]",
            RequestedAt);
}
