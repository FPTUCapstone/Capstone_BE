using System.Globalization;

using FluentAssertions;

using TripMate.Application.Features.Scheduling.Personalization;

namespace TripMate.Application.UnitTests.Features.Scheduling.Personalization;

public sealed class PersonalBehaviorAffinityScorerTests
{
    private const long PoiId = 101;
    private const int CategoryId = 202;

    [Fact]
    public void Score_WithoutEvidence_ReturnsExactNeutralAffinity()
    {
        decimal result = Score();

        result.Should().Be(0.5m);
    }

    [Theory]
    [InlineData(1, 0, 0, "0.6666666666666666666666666666")]
    [InlineData(0, 1, 0, "0.3333333333333333333333333334")]
    [InlineData(0, 0, 1, "0.4")]
    [InlineData(3, 3, 0, "0.5")]
    public void Score_WithDirectEvidence_AppliesDefaultFormula(
        int likeCount,
        int dislikeCount,
        int skipCount,
        string expectedText)
    {
        decimal result = Score(
            poiCounts: new Dictionary<long, PersonalBehaviorCounts>
            {
                [PoiId] = new(likeCount, dislikeCount, skipCount),
            });

        result.Should().Be(decimal.Parse(expectedText, CultureInfo.InvariantCulture));
    }

    [Fact]
    public void Score_WithCustomSkipWeight_AppliesConfiguredWeightToNetAndEvidence()
    {
        var options = new PersonalizationRankingOptions { SkipWeight = 1m };

        decimal result = Score(
            options,
            poiCounts: new Dictionary<long, PersonalBehaviorCounts>
            {
                [PoiId] = new(0, 0, 1),
            });

        result.Should().Be(0.3333333333333333333333333334m);
    }

    [Fact]
    public void Score_WithCustomPriorWeight_AppliesConfiguredPriorToDenominator()
    {
        var options = new PersonalizationRankingOptions { PriorWeight = 1m };

        decimal result = Score(
            options,
            poiCounts: new Dictionary<long, PersonalBehaviorCounts>
            {
                [PoiId] = new(1, 0, 0),
            });

        result.Should().Be(0.75m);
    }

    [Theory]
    [InlineData(1_000_000, 0, 0)]
    [InlineData(0, 1_000_000, 0)]
    public void Score_WithStrongEvidence_RemainsWithinUnitInterval(
        int likeCount,
        int dislikeCount,
        int skipCount)
    {
        decimal result = Score(
            poiCounts: new Dictionary<long, PersonalBehaviorCounts>
            {
                [PoiId] = new(likeCount, dislikeCount, skipCount),
            });

        result.Should().BeInRange(0m, 1m);
    }

    [Theory]
    [InlineData(1, 0, 0, "0.6666666666666666666666666666")]
    [InlineData(0, 1, 0, "0.3333333333333333333333333334")]
    [InlineData(0, 0, 1, "0.4")]
    public void Score_WithDirectEvidence_UsesPoiCounts(
        int likeCount,
        int dislikeCount,
        int skipCount,
        string expectedText)
    {
        decimal result = Score(
            poiCounts: new Dictionary<long, PersonalBehaviorCounts>
            {
                [PoiId] = new(likeCount, dislikeCount, skipCount),
            },
            categoryCounts: new Dictionary<int, CategoryBehaviorCounts>
            {
                [CategoryId] = new(20, 0, 0, 2),
            });

        result.Should().Be(decimal.Parse(expectedText, CultureInfo.InvariantCulture));
    }

    [Theory]
    [InlineData(1, 0, 0, 0, 20, 0, "0.6666666666666666666666666666")]
    [InlineData(0, 1, 0, 20, 0, 0, "0.3333333333333333333333333334")]
    public void Score_WithDirectEvidence_DoesNotBlendEligibleCategoryEvidence(
        int directLikes,
        int directDislikes,
        int directSkips,
        int categoryLikes,
        int categoryDislikes,
        int categorySkips,
        string expectedText)
    {
        decimal result = Score(
            poiCounts: new Dictionary<long, PersonalBehaviorCounts>
            {
                [PoiId] = new(directLikes, directDislikes, directSkips),
            },
            categoryCounts: new Dictionary<int, CategoryBehaviorCounts>
            {
                [CategoryId] = new(
                    categoryLikes,
                    categoryDislikes,
                    categorySkips,
                    DistinctInteractedPoiCount: 2),
            });

        result.Should().Be(decimal.Parse(expectedText, CultureInfo.InvariantCulture));
    }

    [Fact]
    public void Score_WithZeroCountDirectRecord_FallsBackToEligibleCategory()
    {
        decimal result = Score(
            poiCounts: new Dictionary<long, PersonalBehaviorCounts>
            {
                [PoiId] = new(0, 0, 0),
            },
            categoryCounts: new Dictionary<int, CategoryBehaviorCounts>
            {
                [CategoryId] = new(1, 0, 0, 2),
            });

        result.Should().Be(0.6666666666666666666666666666m);
    }

    [Fact]
    public void Score_WithoutPoiEntry_FallsBackToEligibleCategory()
    {
        decimal result = Score(
            poiCounts: new Dictionary<long, PersonalBehaviorCounts>(),
            categoryCounts: new Dictionary<int, CategoryBehaviorCounts>
            {
                [CategoryId] = new(0, 1, 0, 2),
            });

        result.Should().Be(0.3333333333333333333333333334m);
    }

    [Fact]
    public void Score_WithEligibleCategory_AppliesCategoryEventCounts()
    {
        decimal result = Score(
            categoryCounts: new Dictionary<int, CategoryBehaviorCounts>
            {
                [CategoryId] = new(1, 0, 1, 2),
            });

        result.Should().Be(0.5714285714285714285714285714m);
    }

    [Fact]
    public void Score_WithCategoryBelowDistinctThreshold_ReturnsNeutral()
    {
        decimal result = Score(
            categoryCounts: new Dictionary<int, CategoryBehaviorCounts>
            {
                [CategoryId] = new(10, 0, 0, 1),
            });

        result.Should().Be(0.5m);
    }

    [Fact]
    public void Score_WithCategoryAtDistinctThreshold_AppliesCategoryAffinity()
    {
        decimal result = Score(
            categoryCounts: new Dictionary<int, CategoryBehaviorCounts>
            {
                [CategoryId] = new(1, 0, 0, 2),
            });

        result.Should().Be(0.6666666666666666666666666666m);
    }

    [Fact]
    public void Score_WithCustomDistinctThreshold_UsesConfiguredBoundary()
    {
        var options = new PersonalizationRankingOptions
        {
            MinCategoryDistinctPoiCount = 3,
        };
        var categoryCounts = new Dictionary<int, CategoryBehaviorCounts>
        {
            [CategoryId] = new(2, 0, 0, 2),
        };

        decimal belowThreshold = Score(options, categoryCounts: categoryCounts);
        categoryCounts[CategoryId] = new(2, 0, 0, 3);
        decimal atThreshold = Score(options, categoryCounts: categoryCounts);

        belowThreshold.Should().Be(0.5m);
        atThreshold.Should().Be(0.75m);
    }

    [Fact]
    public void Score_WithoutCategoryEntry_ReturnsNeutral()
    {
        decimal result = Score(
            categoryCounts: new Dictionary<int, CategoryBehaviorCounts>
            {
                [CategoryId + 1] = new(5, 0, 0, 2),
            });

        result.Should().Be(0.5m);
    }

    [Fact]
    public void Score_WithDifferentDistinctCounts_UsesEventCountsWithoutDistinctWeighting()
    {
        decimal minimumDistinctResult = Score(
            categoryCounts: new Dictionary<int, CategoryBehaviorCounts>
            {
                [CategoryId] = new(3, 0, 0, 2),
            });
        decimal largerDistinctResult = Score(
            categoryCounts: new Dictionary<int, CategoryBehaviorCounts>
            {
                [CategoryId] = new(3, 0, 0, 200),
            });

        minimumDistinctResult.Should().Be(0.8m);
        largerDistinctResult.Should().Be(minimumDistinctResult);
    }

    [Fact]
    public void Score_WithManyEventsOnMinimumQualifyingDistinctPois_UsesAggregateTotals()
    {
        decimal result = Score(
            categoryCounts: new Dictionary<int, CategoryBehaviorCounts>
            {
                [CategoryId] = new(10, 0, 0, 2),
            });

        result.Should().Be(0.9166666666666666666666666666m);
    }

    [Fact]
    public void Score_WithZeroCountEligibleCategory_ReturnsFormulaNeutral()
    {
        decimal result = Score(
            categoryCounts: new Dictionary<int, CategoryBehaviorCounts>
            {
                [CategoryId] = new(0, 0, 0, 2),
            });

        result.Should().Be(0.5m);
    }

    [Fact]
    public void Score_WithZeroCountDirectAndIneligibleCategory_ReturnsExactNeutral()
    {
        decimal result = Score(
            poiCounts: new Dictionary<long, PersonalBehaviorCounts>
            {
                [PoiId] = new(0, 0, 0),
            },
            categoryCounts: new Dictionary<int, CategoryBehaviorCounts>
            {
                [CategoryId] = new(5, 0, 0, 1),
            });

        result.Should().Be(0.5m);
    }

    [Fact]
    public void Score_WithSameInputs_ReturnsSameResult()
    {
        var options = new PersonalizationRankingOptions();
        var aggregation = CreateAggregation(
            new Dictionary<long, PersonalBehaviorCounts>
            {
                [PoiId] = new(2, 1, 1),
            },
            new Dictionary<int, CategoryBehaviorCounts>
            {
                [CategoryId] = new(10, 0, 0, 2),
            });
        var scorer = new PersonalBehaviorAffinityScorer(options);

        decimal first = scorer.Score(PoiId, CategoryId, aggregation);
        decimal second = scorer.Score(PoiId, CategoryId, aggregation);

        second.Should().Be(first);
    }

    [Fact]
    public void Score_DoesNotMutateAggregationDictionaries()
    {
        var poiCounts = new Dictionary<long, PersonalBehaviorCounts>
        {
            [PoiId] = new(1, 0, 0),
        };
        var categoryCounts = new Dictionary<int, CategoryBehaviorCounts>
        {
            [CategoryId] = new(0, 5, 0, 2),
        };
        var expectedPoiCounts = poiCounts.ToArray();
        var expectedCategoryCounts = categoryCounts.ToArray();

        _ = Score(poiCounts: poiCounts, categoryCounts: categoryCounts);

        poiCounts.Should().Equal(expectedPoiCounts);
        categoryCounts.Should().Equal(expectedCategoryCounts);
    }

    [Fact]
    public void Score_DoesNotMutateOptions()
    {
        var options = new PersonalizationRankingOptions
        {
            SkipWeight = 0.75m,
            PriorWeight = 3m,
            MinCategoryDistinctPoiCount = 4,
        };
        decimal expectedSkipWeight = options.SkipWeight;
        decimal expectedPriorWeight = options.PriorWeight;
        int expectedThreshold = options.MinCategoryDistinctPoiCount;

        _ = Score(
            options,
            poiCounts: new Dictionary<long, PersonalBehaviorCounts>
            {
                [PoiId] = new(0, 0, 2),
            });

        options.SkipWeight.Should().Be(expectedSkipWeight);
        options.PriorWeight.Should().Be(expectedPriorWeight);
        options.MinCategoryDistinctPoiCount.Should().Be(expectedThreshold);
    }

    private static decimal Score(
        PersonalizationRankingOptions? options = null,
        IReadOnlyDictionary<long, PersonalBehaviorCounts>? poiCounts = null,
        IReadOnlyDictionary<int, CategoryBehaviorCounts>? categoryCounts = null)
    {
        var scorer = new PersonalBehaviorAffinityScorer(
            options ?? new PersonalizationRankingOptions());

        return scorer.Score(
            PoiId,
            CategoryId,
            CreateAggregation(poiCounts, categoryCounts));
    }

    private static PersonalBehaviorAggregation CreateAggregation(
        IReadOnlyDictionary<long, PersonalBehaviorCounts>? poiCounts = null,
        IReadOnlyDictionary<int, CategoryBehaviorCounts>? categoryCounts = null) => new(
            poiCounts ?? new Dictionary<long, PersonalBehaviorCounts>(),
            categoryCounts ?? new Dictionary<int, CategoryBehaviorCounts>());
}