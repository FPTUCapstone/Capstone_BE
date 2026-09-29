using FluentAssertions;

using TripMate.Application.Features.Scheduling.Common;
using TripMate.Domain.Entities;

namespace TripMate.Application.UnitTests.Features.Scheduling.Common;

public class TravelerPreferenceScoringTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-json")]
    [InlineData("[]")]
    public void ParsePreferenceTokens_NoUsableTokens_ReturnsEmpty(string? interestTagsJson)
    {
        var tokens = TravelerPreferenceScoring.ParsePreferenceTokens(interestTagsJson);

        tokens.Should().BeEmpty();
    }

    [Fact]
    public void ParsePreferenceTokens_TrimsNormalizesCaseAndFoldsDiacritics()
    {
        var tokens = TravelerPreferenceScoring.ParsePreferenceTokens("[\"  CULT\u00daRE  \"]");

        tokens.Should().Equal("culture");
        tokens.Comparer.Should().BeSameAs(StringComparer.Ordinal);
    }

    [Fact]
    public void CalculatePreferenceScore_MatchingCategory_AddsOneHundred()
    {
        var poi = CreatePoi("Culture");
        var tokens = TravelerPreferenceScoring.ParsePreferenceTokens("[\"culture\"]");

        var score = TravelerPreferenceScoring.CalculatePreferenceScore(poi, tokens);

        score.Should().Be(100);
    }

    [Fact]
    public void CalculatePreferenceScore_NonMatchingCategoryAndUnknownToken_AddZero()
    {
        var poi = CreatePoi("Nature");
        var tokens = TravelerPreferenceScoring.ParsePreferenceTokens("[\"unknown\"]");

        var score = TravelerPreferenceScoring.CalculatePreferenceScore(poi, tokens);

        score.Should().Be(0);
    }

    [Fact]
    public void CalculatePreferenceScore_EachMatchingTagAddsOneHundred()
    {
        var poi = CreatePoi("Nature", "Museum", "Heritage", "Beach");
        var tokens = TravelerPreferenceScoring.ParsePreferenceTokens("[\"museum\",\"heritage\"]");

        var score = TravelerPreferenceScoring.CalculatePreferenceScore(poi, tokens);

        score.Should().Be(200);
    }

    [Fact]
    public void CalculatePreferenceScore_CategoryAndMatchingTagsAccumulate()
    {
        var poi = CreatePoi("Culture", "Museum", "Heritage");
        var tokens = TravelerPreferenceScoring.ParsePreferenceTokens(
            "[\"culture\",\"museum\",\"heritage\",\"unknown\"]");

        var score = TravelerPreferenceScoring.CalculatePreferenceScore(poi, tokens);

        score.Should().Be(300);
    }

    [Fact]
    public void CalculatePreferenceScore_NoPreferenceTokens_ReturnsZero()
    {
        var poi = CreatePoi("Culture", "Museum");

        var score = TravelerPreferenceScoring.CalculatePreferenceScore(poi, new HashSet<string>());

        score.Should().Be(0);
    }

    [Fact]
    public void ParseAndScore_SamePoiAndInput_AreDeterministic()
    {
        var poi = CreatePoi("Culture", "Museum", "Heritage");

        var firstTokens = TravelerPreferenceScoring.ParsePreferenceTokens(
            "[\"culture\",\"museum\"]");
        var secondTokens = TravelerPreferenceScoring.ParsePreferenceTokens(
            "[\"culture\",\"museum\"]");
        var firstScore = TravelerPreferenceScoring.CalculatePreferenceScore(poi, firstTokens);
        var secondScore = TravelerPreferenceScoring.CalculatePreferenceScore(poi, secondTokens);

        secondTokens.Should().BeEquivalentTo(firstTokens);
        secondScore.Should().Be(firstScore).And.Be(200);
    }

    private static PointOfInterest CreatePoi(string categoryName, params string[] tagNames)
    {
        var poi = PointOfInterest.Create(
            PoiCategory.Create(categoryName, null),
            "Test POI",
            16.0471m,
            108.2068m,
            1,
            new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));

        foreach (var tagName in tagNames)
        {
            poi.AddTag(Tag.Create(tagName));
        }

        return poi;
    }
}