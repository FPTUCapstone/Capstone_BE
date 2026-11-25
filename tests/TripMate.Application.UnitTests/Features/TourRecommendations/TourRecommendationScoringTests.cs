using FluentAssertions;

using TripMate.Application.Features.TourRecommendations.Common;
using TripMate.Application.Features.TourRecommendations.Refresh;

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

    [Fact]
    public void Calculate_CandidateWithFreeTextCategoryMatchesTravelerCanonicalInterest()
    {
        // Both sides contain only "culinary" once the reader resolves "Food & Drink",
        // so Jaccard is 1/1. Keeping the original label would make the union size two.
        var canonicalInterest = TourRecommendationCandidateReader.ResolveCanonicalInterestKey("Food & Drink");
        var result = TourRecommendationScoring.Calculate(BaseInput(
            travelerInterests: ["culinary"],
            candidateInterests: [canonicalInterest!]));

        result.Score.Should().Be(100m);
        result.MatchingReasons.Should().Equal("Interest tags");
    }

    [Fact]
    public void Calculate_CandidateWithOnlyLiteralFreeTextCategoryStillScoresZero()
    {
        // Documents the previously-silent failure mode this fix targets: without synonym
        // resolution, a literal mismatch between free-text candidate tokens and canonical
        // traveler keys still yields zero, even though the tour is conceptually a match.
        var result = TourRecommendationScoring.Calculate(BaseInput(
            travelerInterests: ["culinary"],
            candidateInterests: ["Food & Drink"]));

        result.Score.Should().Be(0m);
        result.MatchingReasons.Should().BeEmpty();
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

    [Fact]
    public void Calculate_MatchingPaceAddsWeight()
    {
        var result = TourRecommendationScoring.Calculate(BaseInput(
            travelerInterests: ["beach"], candidateInterests: ["beach"],
            travelerPace: "Moderate", candidatePace: "Moderate"));

        result.Score.Should().Be(100m);
        result.MatchingReasons.Should().Equal("Interest tags", "Pace");
    }

    [Fact]
    public void Calculate_MismatchedPaceContributesZeroButStaysActive()
    {
        var result = TourRecommendationScoring.Calculate(BaseInput(
            travelerInterests: ["beach"], candidateInterests: ["beach"],
            travelerPace: "Relaxed", candidatePace: "Fast"));

        // Interest 40 active + Pace 10 active = 50 active weight; only Interest's 40 scores.
        result.Score.Should().Be(80m);
        result.MatchingReasons.Should().Equal("Interest tags");
    }

    [Fact]
    public void Calculate_AnyTransportOverlapCountsAsFullMatch()
    {
        var result = TourRecommendationScoring.Calculate(BaseInput(
            travelerInterests: ["beach"], candidateInterests: ["beach"],
            travelerTransport: ["Motorbike", "Walking"], candidateTransport: ["Car", "Motorbike"]));

        result.Score.Should().Be(100m);
        result.MatchingReasons.Should().Equal("Interest tags", "Transport");
    }

    [Fact]
    public void Calculate_NoTransportOverlapContributesZeroButStaysActive()
    {
        var result = TourRecommendationScoring.Calculate(BaseInput(
            travelerInterests: ["beach"], candidateInterests: ["beach"],
            travelerTransport: ["Motorbike"], candidateTransport: ["Walking"]));

        result.Score.Should().Be(80m);
        result.MatchingReasons.Should().Equal("Interest tags");
    }

    [Fact]
    public void Calculate_AnyDietOverlapCountsAsFullMatch()
    {
        var result = TourRecommendationScoring.Calculate(BaseInput(
            travelerInterests: ["beach"], candidateInterests: ["beach"],
            travelerDiet: ["Vegetarian", "Halal"], candidateDiet: ["Halal"]));

        result.Score.Should().Be(100m);
        result.MatchingReasons.Should().Equal("Interest tags", "Diet");
    }

    [Fact]
    public void Calculate_PaceTransportDietAbsentOnEitherSideLeavesScoreUnchanged()
    {
        // Regression: a tour/traveler missing these new fields (e.g. every pre-existing row)
        // must score identically to how Calculate behaved before Pace/Transport/Diet existed.
        var withNulls = TourRecommendationScoring.Calculate(BaseInput(
            travelerInterests: ["beach", "food"], candidateInterests: ["beach", "culture"],
            destination: "Đà Nẵng", candidateDestinations: ["Da Nang"],
            hasDateCriteria: true, dateMatches: true));
        var withoutOptionalDimensions = TourRecommendationScoring.Calculate(new TourRecommendationScoreInput(
            TravelerInterestTokens: ["beach", "food"],
            CandidateInterestTokens: ["beach", "culture"],
            RequestedDestination: "Đà Nẵng",
            CandidateDestinations: ["Da Nang"],
            HasDateCriteria: true,
            DateMatches: true,
            MinBudget: null,
            MaxBudget: null,
            CandidatePrice: 1m,
            CompletedHistoryTokens: [],
            CandidateHistoryTokens: []));

        withNulls.Score.Should().Be(withoutOptionalDimensions.Score);
        withNulls.MatchingReasons.Should().Equal(withoutOptionalDimensions.MatchingReasons);
    }

    private static TourRecommendationScoreInput BaseInput(
        IReadOnlyCollection<string>? travelerInterests = null,
        IReadOnlyCollection<string>? candidateInterests = null,
        string? destination = null,
        IReadOnlyCollection<string>? candidateDestinations = null,
        bool hasDateCriteria = false,
        bool dateMatches = false,
        string? travelerPace = null,
        string? candidatePace = null,
        IReadOnlyCollection<string>? travelerTransport = null,
        IReadOnlyCollection<string>? candidateTransport = null,
        IReadOnlyCollection<string>? travelerDiet = null,
        IReadOnlyCollection<string>? candidateDiet = null) => new(
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
            CandidateHistoryTokens: [],
            TravelerPace: travelerPace,
            CandidatePace: candidatePace,
            TravelerTransportTokens: travelerTransport,
            CandidateTransportTokens: candidateTransport,
            TravelerDietTokens: travelerDiet,
            CandidateDietTokens: candidateDiet);
}