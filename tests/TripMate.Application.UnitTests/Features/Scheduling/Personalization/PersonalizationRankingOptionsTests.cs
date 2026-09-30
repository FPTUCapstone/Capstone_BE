using FluentAssertions;

using Microsoft.Extensions.Options;

using TripMate.Application.Features.Scheduling.Common;
using TripMate.Application.Features.Scheduling.Personalization;

namespace TripMate.Application.UnitTests.Features.Scheduling.Personalization;

public sealed class PersonalizationRankingOptionsTests
{
    [Fact]
    public void Defaults_MatchFrozenSpecification()
    {
        var options = new PersonalizationRankingOptions();

        PersonalizationRankingOptions.SectionName.Should().Be("Personalization");
        options.CategoryAffinityWeight.Should().Be(0.300m);
        options.TagAffinityWeight.Should().Be(0.200m);
        options.BehaviorAffinityWeight.Should().Be(0.250m);
        options.ScenicQualityWeight.Should().Be(0.125m);
        options.PhotoQualityWeight.Should().Be(0.125m);
        options.BaseWeight.Should().Be(0.60m);
        options.AiWeight.Should().Be(0.40m);
        options.SkipWeight.Should().Be(0.5m);
        options.PriorWeight.Should().Be(2.0m);
        options.MinCategoryDistinctPoiCount.Should().Be(2);
        options.MaxProviderCandidates.Should().Be(60);
        options.ProviderTimeout.Should().Be(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void Validate_DefaultOptions_Succeeds()
    {
        Validate(ValidOptions()).Should().Be(ValidateOptionsResult.Success);
    }

    [Theory]
    [InlineData(nameof(PersonalizationRankingOptions.CategoryAffinityWeight))]
    [InlineData(nameof(PersonalizationRankingOptions.TagAffinityWeight))]
    [InlineData(nameof(PersonalizationRankingOptions.BehaviorAffinityWeight))]
    [InlineData(nameof(PersonalizationRankingOptions.ScenicQualityWeight))]
    [InlineData(nameof(PersonalizationRankingOptions.PhotoQualityWeight))]
    public void Validate_NegativeBaseComponentWeight_FailsForThatProperty(string propertyName)
    {
        PersonalizationRankingOptions options = OptionsWithNegativeBaseWeight(propertyName);

        Validate(options).FailureMessage.Should().Contain(propertyName);
    }

    [Theory]
    [InlineData("0.301", true)]
    [InlineData("0.3005", true)]
    [InlineData("0.3011", false)]
    public void Validate_BaseWeightSum_UsesInclusivePointZeroZeroOneTolerance(
        string categoryWeight,
        bool succeeds)
    {
        PersonalizationRankingOptions options = ValidOptions(
            categoryAffinityWeight: decimal.Parse(
                categoryWeight,
                System.Globalization.CultureInfo.InvariantCulture));

        Validate(options).Succeeded.Should().Be(succeeds);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Validate_NegativeFusionWeight_FailsForThatProperty(bool baseWeightIsNegative)
    {
        PersonalizationRankingOptions options = baseWeightIsNegative
            ? ValidOptions(baseWeight: -0.001m, aiWeight: 1.001m)
            : ValidOptions(baseWeight: 1.001m, aiWeight: -0.001m);
        string propertyName = baseWeightIsNegative
            ? nameof(PersonalizationRankingOptions.BaseWeight)
            : nameof(PersonalizationRankingOptions.AiWeight);

        Validate(options).FailureMessage.Should().Contain(propertyName);
    }

    [Theory]
    [InlineData("0.601", true)]
    [InlineData("0.6005", true)]
    [InlineData("0.6011", false)]
    public void Validate_FusionWeightSum_UsesInclusivePointZeroZeroOneTolerance(
        string baseWeight,
        bool succeeds)
    {
        PersonalizationRankingOptions options = ValidOptions(
            baseWeight: decimal.Parse(
                baseWeight,
                System.Globalization.CultureInfo.InvariantCulture));

        Validate(options).Succeeded.Should().Be(succeeds);
    }

    [Theory]
    [InlineData("0", true)]
    [InlineData("-0.001", false)]
    public void Validate_SkipWeight_RequiresNonNegativeValue(string value, bool succeeds)
    {
        PersonalizationRankingOptions options = ValidOptions(
            skipWeight: decimal.Parse(value, System.Globalization.CultureInfo.InvariantCulture));

        Validate(options).Succeeded.Should().Be(succeeds);
    }

    [Theory]
    [InlineData("0.001", true)]
    [InlineData("0", false)]
    [InlineData("-0.001", false)]
    public void Validate_PriorWeight_RequiresPositiveValue(string value, bool succeeds)
    {
        PersonalizationRankingOptions options = ValidOptions(
            priorWeight: decimal.Parse(value, System.Globalization.CultureInfo.InvariantCulture));

        Validate(options).Succeeded.Should().Be(succeeds);
    }

    [Theory]
    [InlineData(1, true)]
    [InlineData(0, false)]
    public void Validate_MinCategoryDistinctPoiCount_RequiresAtLeastOne(
        int value,
        bool succeeds)
    {
        Validate(ValidOptions(minCategoryDistinctPoiCount: value)).Succeeded.Should().Be(succeeds);
    }

    [Fact]
    public void Validate_MaxProviderCandidatesEqualToEffectiveMatrixLimit_Succeeds()
    {
        var schedulingOptions = new SchedulingGenerationOptions();
        PersonalizationRankingOptions options = ValidOptions(
            maxProviderCandidates: schedulingOptions.EffectiveMaxMatrixCandidates);

        Validate(options, schedulingOptions).Should().Be(ValidateOptionsResult.Success);
    }

    [Fact]
    public void Validate_MaxProviderCandidatesBelowEffectiveMatrixLimit_Fails()
    {
        var schedulingOptions = new SchedulingGenerationOptions();
        PersonalizationRankingOptions options = ValidOptions(
            maxProviderCandidates: schedulingOptions.EffectiveMaxMatrixCandidates - 1);

        Validate(options, schedulingOptions).FailureMessage.Should().Contain(
            nameof(PersonalizationRankingOptions.MaxProviderCandidates));
    }

    [Fact]
    public void Validate_CustomMatrixLimit_UsesComputedEffectiveLimit()
    {
        var schedulingOptions = new SchedulingGenerationOptions
        {
            MaxMatrixCandidates = 75,
        };
        PersonalizationRankingOptions options = ValidOptions(maxProviderCandidates: 75);

        Validate(options, schedulingOptions).Should().Be(ValidateOptionsResult.Success);
    }

    [Fact]
    public void Validate_MatrixLimitBelowExistingMinimum_UsesSchedulingFallbackProperty()
    {
        var schedulingOptions = new SchedulingGenerationOptions
        {
            MaxMatrixCandidates = SchedulingGenerationOptions.MinimumMaxMatrixCandidates - 1,
        };
        PersonalizationRankingOptions options = ValidOptions(
            maxProviderCandidates: schedulingOptions.EffectiveMaxMatrixCandidates - 1);

        schedulingOptions.EffectiveMaxMatrixCandidates.Should().Be(
            SchedulingGenerationOptions.DefaultMaxMatrixCandidates);
        Validate(options, schedulingOptions).Failed.Should().BeTrue();
    }

    [Theory]
    [InlineData(1, true)]
    [InlineData(0, false)]
    [InlineData(-1, false)]
    public void Validate_ProviderTimeout_RequiresPositiveDuration(int seconds, bool succeeds)
    {
        PersonalizationRankingOptions options = ValidOptions(
            providerTimeout: TimeSpan.FromSeconds(seconds));

        Validate(options).Succeeded.Should().Be(succeeds);
    }

    private static ValidateOptionsResult Validate(
        PersonalizationRankingOptions options,
        SchedulingGenerationOptions? schedulingOptions = null) =>
        new PersonalizationRankingOptionsValidator(
            schedulingOptions ?? new SchedulingGenerationOptions())
        .Validate(null, options);

    private static PersonalizationRankingOptions OptionsWithNegativeBaseWeight(
        string propertyName) => propertyName switch
        {
            nameof(PersonalizationRankingOptions.CategoryAffinityWeight) => ValidOptions(
                categoryAffinityWeight: -0.001m,
                tagAffinityWeight: 0.501m),
            nameof(PersonalizationRankingOptions.TagAffinityWeight) => ValidOptions(
                categoryAffinityWeight: 0.501m,
                tagAffinityWeight: -0.001m),
            nameof(PersonalizationRankingOptions.BehaviorAffinityWeight) => ValidOptions(
                categoryAffinityWeight: 0.551m,
                behaviorAffinityWeight: -0.001m),
            nameof(PersonalizationRankingOptions.ScenicQualityWeight) => ValidOptions(
                categoryAffinityWeight: 0.426m,
                scenicQualityWeight: -0.001m),
            nameof(PersonalizationRankingOptions.PhotoQualityWeight) => ValidOptions(
                categoryAffinityWeight: 0.426m,
                photoQualityWeight: -0.001m),
            _ => throw new ArgumentOutOfRangeException(nameof(propertyName)),
        };

    private static PersonalizationRankingOptions ValidOptions(
        decimal categoryAffinityWeight = 0.300m,
        decimal tagAffinityWeight = 0.200m,
        decimal behaviorAffinityWeight = 0.250m,
        decimal scenicQualityWeight = 0.125m,
        decimal photoQualityWeight = 0.125m,
        decimal baseWeight = 0.60m,
        decimal aiWeight = 0.40m,
        decimal skipWeight = 0.5m,
        decimal priorWeight = 2.0m,
        int minCategoryDistinctPoiCount = 2,
        int maxProviderCandidates = 60,
        TimeSpan? providerTimeout = null) => new()
        {
            CategoryAffinityWeight = categoryAffinityWeight,
            TagAffinityWeight = tagAffinityWeight,
            BehaviorAffinityWeight = behaviorAffinityWeight,
            ScenicQualityWeight = scenicQualityWeight,
            PhotoQualityWeight = photoQualityWeight,
            BaseWeight = baseWeight,
            AiWeight = aiWeight,
            SkipWeight = skipWeight,
            PriorWeight = priorWeight,
            MinCategoryDistinctPoiCount = minCategoryDistinctPoiCount,
            MaxProviderCandidates = maxProviderCandidates,
            ProviderTimeout = providerTimeout ?? TimeSpan.FromSeconds(5),
        };
}