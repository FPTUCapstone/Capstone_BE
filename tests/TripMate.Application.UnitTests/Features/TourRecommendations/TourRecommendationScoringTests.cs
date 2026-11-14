using FluentAssertions;

using TripMate.Application.Features.TourRecommendations.Common;

namespace TripMate.Application.UnitTests.Features.TourRecommendations;

public sealed class TourRecommendationScoringTests
{
    [Fact]
    public void NormalizeTokens_RemovesDiacriticsWhitespaceCaseAndDuplicates()
    {
        TourRecommendationScoring.NormalizeTokens(["  Biển ", "BIEN", "", " Văn hóa "])
            .Should().Equal("bien", "van hoa");
    }

    [Fact]
    public void Jaccard_UsesDistinctNormalizedUnionAndIntersection()
    {
        TourRecommendationScoring.Jaccard(["Beach", "food"], [" beach ", "culture"])
            .Should().BeApproximately(1m / 3m, 0.000001m);
    }

    [Fact]
    public void Calculate_WithAllInputs_UsesApprovedWeightsAndReasonOrder()
    {
        var result = TourRecommendationScoring.Calculate(new TourRecommendationScoreInput(
            TravelerInterestTokens: ["beach", "food"],
            CandidateInterestTokens: ["beach", "culture"],
            RequestedDestination: "Đà Nẵng",
            CandidateDestinations: ["Da Nang"],
            HasDateCriteria: true,
            DateMatches: true,
            MinBudget: 1_000_000m,
            MaxBudget: 3_000_000m,
            CandidatePrice: 2_000_000m,
            CompletedHistoryTokens: ["adventure", "Da Nang"],
            CandidateHistoryTokens: ["Da Nang", "culture"]));

        result.Score.Should().Be(66.67m);
        result.MatchingReasons.Should().Equal(
            "Interest tags", "Destination", "Date", "Budget", "Trip history");
    }

    [Fact]
    public void Calculate_WithoutOptionalCriteriaOrHistory_RenormalizesInterestToOneHundred()
    {
        var result = TourRecommendationScoring.Calculate(BaseInput(
            travelerInterests: ["beach"], candidateInterests: ["beach"]));

        result.Score.Should().Be(100m);
        result.MatchingReasons.Should().Equal("Interest tags");
    }

    [Fact]
    public void Calculate_WithoutHistory_RenormalizesOnlyActiveCriteria()
    {
        var result = TourRecommendationScoring.Calculate(BaseInput(
            travelerInterests: ["beach"],
            candidateInterests: ["beach"],
            destination: "Hue",
            candidateDestinations: ["Da Nang"],
            hasDateCriteria: true,
            dateMatches: true));

        result.Score.Should().Be(73.33m);
        result.MatchingReasons.Should().Equal("Interest tags", "Date");
    }

    [Fact]
    public void Calculate_DateMismatchContributesZeroButDoesNotRejectCandidate()
    {
        var result = TourRecommendationScoring.Calculate(BaseInput(
            travelerInterests: ["beach"],
            candidateInterests: ["beach"],
            hasDateCriteria: true,
            dateMatches: false));

        result.Score.Should().Be(72.73m);
    }

    [Theory]
    [InlineData("80.00", false)]
    [InlineData("80.01", true)]
    public void Qualifies_UsesStrictEffectiveThreshold(string scoreText, bool expected)
    {
        decimal score = decimal.Parse(scoreText, System.Globalization.CultureInfo.InvariantCulture);

        TourRecommendationScoring.Qualifies(score, configuredThreshold: 50m).Should().Be(expected);
    }

    [Fact]
    public void Qualifies_HonorsConfiguredThresholdAboveFloor()
    {
        TourRecommendationScoring.Qualifies(85m, configuredThreshold: 90m).Should().BeFalse();
        TourRecommendationScoring.Qualifies(90.01m, configuredThreshold: 90m).Should().BeTrue();
    }

    [Fact]
    public void Calculate_RoundsOnceAwayFromZeroAtTwoDecimals()
    {
        var result = TourRecommendationScoring.Calculate(BaseInput(
            travelerInterests: ["a", "b", "c", "d", "e", "f", "g", "h"],
            candidateInterests: ["a", "b", "c", "d", "e"]));

        result.Score.Should().Be(62.50m);
    }

    private static TourRecommendationScoreInput BaseInput(
        IReadOnlyCollection<string>? travelerInterests = null,
        IReadOnlyCollection<string>? candidateInterests = null,
        string? destination = null,
        IReadOnlyCollection<string>? candidateDestinations = null,
        bool hasDateCriteria = false,
        bool dateMatches = false) => new(
            travelerInterests ?? ["beach"],
            candidateInterests ?? ["beach"],
            destination,
            candidateDestinations ?? [],
            hasDateCriteria,
            dateMatches,
            MinBudget: null,
            MaxBudget: null,
            CandidatePrice: 1m,
            CompletedHistoryTokens: [],
            CandidateHistoryTokens: []);
}