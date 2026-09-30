using System.Reflection;

using FluentAssertions;

using TripMate.Application.Features.Scheduling.Personalization;

namespace TripMate.Application.UnitTests.Features.Scheduling.Personalization;

public sealed class PersonalizationFeatureBuilderTests
{
    [Fact]
    public void Build_WithExactCategoryPreference_ReturnsCategoryAffinityOne()
    {
        PersonalizationPoiFeatures result = Build(["culture"], "culture", []);

        result.CategoryAffinity.Should().Be(1m);
    }

    [Fact]
    public void Build_WithoutCategoryPreference_ReturnsCategoryAffinityZero()
    {
        PersonalizationPoiFeatures result = Build(["nature"], "culture", []);

        result.CategoryAffinity.Should().Be(0m);
    }

    [Fact]
    public void Build_WithCategoryCaseVariant_ReturnsCategoryAffinityOne()
    {
        PersonalizationPoiFeatures result = Build(["culture"], "CULTURE", []);

        result.CategoryAffinity.Should().Be(1m);
    }

    [Fact]
    public void Build_WithCategoryDiacriticVariant_ReturnsCategoryAffinityOne()
    {
        PersonalizationPoiFeatures result = Build(["cafe"], "Café", []);

        result.CategoryAffinity.Should().Be(1m);
    }

    [Fact]
    public void Build_WithoutPreferenceTokens_ReturnsCategoryAffinityZero()
    {
        PersonalizationPoiFeatures result = Build([], "culture", []);

        result.CategoryAffinity.Should().Be(0m);
    }

    [Fact]
    public void Build_WithCategoryMatchOnly_DoesNotProduceTagAffinity()
    {
        PersonalizationPoiFeatures result = Build(["cafe"], "Café", ["CAFE"]);

        result.CategoryAffinity.Should().Be(1m);
        result.TagAffinity.Should().Be(0m);
    }

    [Fact]
    public void Build_WithoutMatchingTags_ReturnsTagAffinityZero()
    {
        PersonalizationPoiFeatures result = Build(["culture"], "nature", ["outdoor"]);

        result.TagAffinity.Should().Be(0m);
    }

    [Fact]
    public void Build_WithOneUniqueMatchingTag_ReturnsOneThirdTagAffinity()
    {
        PersonalizationPoiFeatures result = Build(["culture"], "nature", ["culture"]);

        result.TagAffinity.Should().Be(1m / 3m);
    }

    [Fact]
    public void Build_WithTwoUniqueMatchingTags_ReturnsTwoThirdsTagAffinity()
    {
        PersonalizationPoiFeatures result = Build(
            ["culture", "heritage"],
            "nature",
            ["culture", "heritage"]);

        result.TagAffinity.Should().Be(2m / 3m);
    }

    [Fact]
    public void Build_WithThreeUniqueMatchingTags_ReturnsFullTagAffinity()
    {
        PersonalizationPoiFeatures result = Build(
            ["culture", "heritage", "museum"],
            "nature",
            ["culture", "heritage", "museum"]);

        result.TagAffinity.Should().Be(1m);
    }

    [Fact]
    public void Build_WithMoreThanThreeUniqueMatchingTags_SaturatesTagAffinityAtOne()
    {
        PersonalizationPoiFeatures result = Build(
            ["culture", "heritage", "museum", "history"],
            "nature",
            ["culture", "heritage", "museum", "history"]);

        result.TagAffinity.Should().Be(1m);
    }

    [Fact]
    public void Build_WithDuplicateRawMatchingTags_CountsTagOnce()
    {
        PersonalizationPoiFeatures result = Build(
            ["culture"],
            "nature",
            ["culture", "culture"]);

        result.TagAffinity.Should().Be(1m / 3m);
    }

    [Fact]
    public void Build_WithCaseVariantMatchingTags_CountsNormalizedTagOnce()
    {
        PersonalizationPoiFeatures result = Build(
            ["culture"],
            "nature",
            ["culture", "CULTURE"]);

        result.TagAffinity.Should().Be(1m / 3m);
    }

    [Fact]
    public void Build_WithDiacriticVariantMatchingTags_CountsNormalizedTagOnce()
    {
        PersonalizationPoiFeatures result = Build(
            ["cafe"],
            "nature",
            ["Café", "CAFE"]);

        result.TagAffinity.Should().Be(1m / 3m);
    }

    [Fact]
    public void Build_WithTagEqualToNormalizedCategory_ExcludesCategoryTag()
    {
        PersonalizationPoiFeatures result = Build(["cafe"], "Café", ["CAFE"]);

        result.TagAffinity.Should().Be(0m);
    }

    [Fact]
    public void Build_WithRepeatedCategoryEquivalentTags_ContributesNoTagAffinity()
    {
        PersonalizationPoiFeatures result = Build(
            ["cafe"],
            "Café",
            ["cafe", "CAFE", "Café"]);

        result.TagAffinity.Should().Be(0m);
    }

    [Fact]
    public void Build_WithCategoryTagAndIndependentMatch_CountsOnlyIndependentTag()
    {
        PersonalizationPoiFeatures result = Build(
            ["cafe", "dessert"],
            "Café",
            ["CAFE", "dessert"]);

        result.TagAffinity.Should().Be(1m / 3m);
    }

    [Fact]
    public void Build_WithDistinctIndependentMatchingTags_KeepsBothMatches()
    {
        PersonalizationPoiFeatures result = Build(
            ["nature", "outdoor"],
            "park",
            ["nature", "outdoor"]);

        result.TagAffinity.Should().Be(2m / 3m);
    }

    [Fact]
    public void Build_WithNullScenicScore_ReturnsNeutralScenicQuality()
    {
        PersonalizationPoiFeatures result = Build([], "nature", [], scenicScore: null);

        result.ScenicQuality.Should().Be(0.5m);
    }

    [Fact]
    public void Build_WithZeroScenicScore_ReturnsZeroScenicQuality()
    {
        PersonalizationPoiFeatures result = Build([], "nature", [], scenicScore: 0m);

        result.ScenicQuality.Should().Be(0m);
    }

    [Fact]
    public void Build_WithFiveScenicScore_ReturnsHalfScenicQuality()
    {
        PersonalizationPoiFeatures result = Build([], "nature", [], scenicScore: 5m);

        result.ScenicQuality.Should().Be(0.5m);
    }

    [Fact]
    public void Build_WithSevenPointFiveScenicScore_ReturnsThreeQuarterScenicQuality()
    {
        PersonalizationPoiFeatures result = Build([], "nature", [], scenicScore: 7.5m);

        result.ScenicQuality.Should().Be(0.75m);
    }

    [Fact]
    public void Build_WithTenScenicScore_ReturnsFullScenicQuality()
    {
        PersonalizationPoiFeatures result = Build([], "nature", [], scenicScore: 10m);

        result.ScenicQuality.Should().Be(1m);
    }

    [Fact]
    public void Build_WithNullPhotoRating_ReturnsNeutralPhotoQuality()
    {
        PersonalizationPoiFeatures result = Build([], "nature", [], photoRating: null);

        result.PhotoQuality.Should().Be(0.5m);
    }

    [Fact]
    public void Build_WithZeroPhotoRating_ReturnsZeroPhotoQuality()
    {
        PersonalizationPoiFeatures result = Build([], "nature", [], photoRating: 0m);

        result.PhotoQuality.Should().Be(0m);
    }

    [Fact]
    public void Build_WithFivePhotoRating_ReturnsHalfPhotoQuality()
    {
        PersonalizationPoiFeatures result = Build([], "nature", [], photoRating: 5m);

        result.PhotoQuality.Should().Be(0.5m);
    }

    [Fact]
    public void Build_WithSevenPointFivePhotoRating_ReturnsThreeQuarterPhotoQuality()
    {
        PersonalizationPoiFeatures result = Build([], "nature", [], photoRating: 7.5m);

        result.PhotoQuality.Should().Be(0.75m);
    }

    [Fact]
    public void Build_WithTenPhotoRating_ReturnsFullPhotoQuality()
    {
        PersonalizationPoiFeatures result = Build([], "nature", [], photoRating: 10m);

        result.PhotoQuality.Should().Be(1m);
    }

    [Fact]
    public void Build_WithSameInputs_ReturnsEqualFeatureResults()
    {
        string[] preferences = ["cafe", "dessert"];
        string[] tags = ["CAFE", "dessert"];

        PersonalizationPoiFeatures first = Build(
            preferences,
            "Café",
            tags,
            scenicScore: 7.5m,
            photoRating: 5m);
        PersonalizationPoiFeatures second = Build(
            preferences,
            "Café",
            tags,
            scenicScore: 7.5m,
            photoRating: 5m);

        second.Should().Be(first);
    }

    [Fact]
    public void Build_WithCallerOwnedTags_DoesNotMutateTagCollection()
    {
        var tags = new List<string> { "dessert", "CAFE", "dessert" };

        _ = Build(["cafe", "dessert"], "Café", tags);

        tags.Should().Equal("dessert", "CAFE", "dessert");
    }

    [Fact]
    public void Build_WithCallerOwnedPreferences_DoesNotMutatePreferenceCollection()
    {
        var preferences = new List<string> { "cafe", "dessert", "dessert" };

        _ = Build(preferences, "Café", ["dessert"]);

        preferences.Should().Equal("cafe", "dessert", "dessert");
    }

    [Fact]
    public void Build_WithCategoryAndIndependentTagMatches_KeepsAffinitiesIndependent()
    {
        PersonalizationPoiFeatures result = Build(
            ["cafe", "dessert"],
            "Café",
            ["CAFE", "dessert"]);

        result.CategoryAffinity.Should().Be(1m);
        result.TagAffinity.Should().Be(1m / 3m);
    }

    [Fact]
    public void PersonalizationPoiFeatures_PublicSurface_ContainsOnlyApprovedFeatures()
    {
        string[] propertyNames = typeof(PersonalizationPoiFeatures)
            .GetProperties(BindingFlags.Instance | BindingFlags.Public)
            .Select(property => property.Name)
            .ToArray();

        propertyNames.Should().BeEquivalentTo(
            nameof(PersonalizationPoiFeatures.CategoryAffinity),
            nameof(PersonalizationPoiFeatures.TagAffinity),
            nameof(PersonalizationPoiFeatures.ScenicQuality),
            nameof(PersonalizationPoiFeatures.PhotoQuality));
    }

    private static PersonalizationPoiFeatures Build(
        IReadOnlyCollection<string> preferenceTokens,
        string categoryName,
        IReadOnlyCollection<string> tagNames,
        decimal? scenicScore = null,
        decimal? photoRating = null) => PersonalizationFeatureBuilder.Build(
            preferenceTokens,
            categoryName,
            tagNames,
            scenicScore,
            photoRating);
}