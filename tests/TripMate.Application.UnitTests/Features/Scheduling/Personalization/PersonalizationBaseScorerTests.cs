using FluentAssertions;

using TripMate.Application.Features.Scheduling.Personalization;

namespace TripMate.Application.UnitTests.Features.Scheduling.Personalization;

public sealed class PersonalizationBaseScorerTests
{
    [Fact]
    public void Score_WithAllInputsZero_ReturnsZero()
    {
        decimal result = Score(
            new PersonalizationPoiFeatures(
                CategoryAffinity: 0m,
                TagAffinity: 0m,
                ScenicQuality: 0m,
                PhotoQuality: 0m),
            personalBehaviorAffinity: 0m);

        result.Should().Be(0m);
    }

    [Fact]
    public void Score_WithAllInputsOne_ReturnsOne()
    {
        decimal result = Score(Features(1m, 1m, 1m, 1m), 1m);

        result.Should().Be(1m);
    }

    [Fact]
    public void Score_WithOnlyCategoryAffinity_ReturnsCategoryWeight()
    {
        decimal result = Score(Features(categoryAffinity: 1m), 0m);

        result.Should().Be(0.300m);
    }

    [Fact]
    public void Score_WithOnlyTagAffinity_ReturnsTagWeight()
    {
        decimal result = Score(Features(tagAffinity: 1m), 0m);

        result.Should().Be(0.200m);
    }

    [Fact]
    public void Score_WithOnlyBehaviorAffinity_ReturnsBehaviorWeight()
    {
        decimal result = Score(Features(), 1m);

        result.Should().Be(0.250m);
    }

    [Fact]
    public void Score_WithOnlyScenicQuality_ReturnsScenicWeight()
    {
        decimal result = Score(Features(scenicQuality: 1m), 0m);

        result.Should().Be(0.125m);
    }

    [Fact]
    public void Score_WithOnlyPhotoQuality_ReturnsPhotoWeight()
    {
        decimal result = Score(Features(photoQuality: 1m), 0m);

        result.Should().Be(0.125m);
    }

    [Fact]
    public void Score_WithMixedRepresentativeValues_ReturnsExactWeightedSum()
    {
        decimal result = Score(
            Features(
                categoryAffinity: 0.8m,
                tagAffinity: 0.6m,
                scenicQuality: 0.2m,
                photoQuality: 1m),
            personalBehaviorAffinity: 0.4m);

        result.Should().Be(0.61m);
    }

    [Fact]
    public void Score_WithNeutralBehavior_ContributesHalfBehaviorWeight()
    {
        decimal result = Score(Features(), personalBehaviorAffinity: 0.5m);

        result.Should().Be(0.125m);
    }

    [Fact]
    public void Score_WithoutPreferenceFeatures_KeepsBehaviorAndQualityContributions()
    {
        decimal result = Score(
            Features(scenicQuality: 0.6m, photoQuality: 0.4m),
            personalBehaviorAffinity: 0.8m);

        result.Should().Be(0.325m);
    }

    [Fact]
    public void Score_WithCustomCategoryWeight_UsesConfiguredWeight()
    {
        var options = CreateOptions(
            categoryWeight: 0.4m,
            tagWeight: 0.1m,
            behaviorWeight: 0.2m,
            scenicWeight: 0.15m,
            photoWeight: 0.15m);

        decimal result = Score(Features(categoryAffinity: 1m), 0m, options);

        result.Should().Be(0.4m);
    }

    [Fact]
    public void Score_WithCustomBehaviorWeight_UsesConfiguredWeight()
    {
        var options = CreateOptions(
            categoryWeight: 0.2m,
            tagWeight: 0.1m,
            behaviorWeight: 0.4m,
            scenicWeight: 0.15m,
            photoWeight: 0.15m);

        decimal result = Score(Features(), 1m, options);

        result.Should().Be(0.4m);
    }

    [Fact]
    public void Score_WithRedistributedValidWeights_ReturnsExactWeightedSum()
    {
        var options = CreateOptions(
            categoryWeight: 0.1m,
            tagWeight: 0.15m,
            behaviorWeight: 0.2m,
            scenicWeight: 0.25m,
            photoWeight: 0.3m);

        decimal result = Score(
            Features(
                categoryAffinity: 0.2m,
                tagAffinity: 0.4m,
                scenicQuality: 0.8m,
                photoQuality: 1m),
            personalBehaviorAffinity: 0.6m,
            options);

        result.Should().Be(0.7m);
    }

    [Fact]
    public void Score_WithStrongNonPreferenceSignals_CanOutrankCategoryMatch()
    {
        decimal preferenceMatchScore = Score(
            Features(categoryAffinity: 1m),
            personalBehaviorAffinity: 0m);
        decimal highSignalScore = Score(
            Features(
                categoryAffinity: 0m,
                tagAffinity: 1m,
                scenicQuality: 1m,
                photoQuality: 1m),
            personalBehaviorAffinity: 1m);

        preferenceMatchScore.Should().Be(0.300m);
        highSignalScore.Should().Be(0.700m);
        highSignalScore.Should().BeGreaterThan(preferenceMatchScore);
    }

    [Fact]
    public void Score_WithSameInputs_ReturnsSameResult()
    {
        var options = new PersonalizationRankingOptions();
        PersonalizationPoiFeatures features = Features(0.8m, 0.6m, 0.2m, 1m);
        var scorer = new PersonalizationBaseScorer(options);

        decimal first = scorer.Score(features, 0.4m);
        decimal second = scorer.Score(features, 0.4m);

        second.Should().Be(first);
    }

    [Fact]
    public void Score_DoesNotMutateFeatures()
    {
        PersonalizationPoiFeatures features = Features(0.8m, 0.6m, 0.2m, 1m);
        PersonalizationPoiFeatures expected = features with { };

        _ = Score(features, 0.4m);

        features.Should().Be(expected);
    }

    [Fact]
    public void Score_DoesNotMutateOptions()
    {
        var options = CreateOptions(0.1m, 0.15m, 0.2m, 0.25m, 0.3m);
        decimal expectedCategoryWeight = options.CategoryAffinityWeight;
        decimal expectedTagWeight = options.TagAffinityWeight;
        decimal expectedBehaviorWeight = options.BehaviorAffinityWeight;
        decimal expectedScenicWeight = options.ScenicQualityWeight;
        decimal expectedPhotoWeight = options.PhotoQualityWeight;

        _ = Score(Features(0.2m, 0.4m, 0.8m, 1m), 0.6m, options);

        options.CategoryAffinityWeight.Should().Be(expectedCategoryWeight);
        options.TagAffinityWeight.Should().Be(expectedTagWeight);
        options.BehaviorAffinityWeight.Should().Be(expectedBehaviorWeight);
        options.ScenicQualityWeight.Should().Be(expectedScenicWeight);
        options.PhotoQualityWeight.Should().Be(expectedPhotoWeight);
    }

    [Fact]
    public void Score_WithRepresentativeValidInputs_RemainsWithinUnitInterval()
    {
        decimal lowScore = Score(Features(0.1m, 0.2m, 0.1m, 0.2m), 0.1m);
        decimal highScore = Score(Features(0.9m, 0.8m, 1m, 0.9m), 0.95m);

        lowScore.Should().BeInRange(0m, 1m);
        highScore.Should().BeInRange(0m, 1m);
    }

    private static decimal Score(
        PersonalizationPoiFeatures features,
        decimal personalBehaviorAffinity,
        PersonalizationRankingOptions? options = null)
    {
        var scorer = new PersonalizationBaseScorer(
            options ?? new PersonalizationRankingOptions());

        return scorer.Score(features, personalBehaviorAffinity);
    }

    private static PersonalizationPoiFeatures Features(
        decimal categoryAffinity = 0m,
        decimal tagAffinity = 0m,
        decimal scenicQuality = 0m,
        decimal photoQuality = 0m) => new(
            categoryAffinity,
            tagAffinity,
            scenicQuality,
            photoQuality);

    private static PersonalizationRankingOptions CreateOptions(
        decimal categoryWeight,
        decimal tagWeight,
        decimal behaviorWeight,
        decimal scenicWeight,
        decimal photoWeight) => new()
        {
            CategoryAffinityWeight = categoryWeight,
            TagAffinityWeight = tagWeight,
            BehaviorAffinityWeight = behaviorWeight,
            ScenicQualityWeight = scenicWeight,
            PhotoQualityWeight = photoWeight,
        };
}