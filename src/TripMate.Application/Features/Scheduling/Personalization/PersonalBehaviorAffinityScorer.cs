namespace TripMate.Application.Features.Scheduling.Personalization;

internal sealed class PersonalBehaviorAffinityScorer
{
    private const decimal NeutralAffinity = 0.5m;

    private readonly PersonalizationRankingOptions _options;

    public PersonalBehaviorAffinityScorer(PersonalizationRankingOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    public decimal Score(
        long poiId,
        int categoryId,
        PersonalBehaviorAggregation aggregation)
    {
        ArgumentNullException.ThrowIfNull(aggregation);

        if (aggregation.PoiCounts.TryGetValue(poiId, out PersonalBehaviorCounts? directCounts)
            && HasEvidence(
                directCounts.LikeCount,
                directCounts.DislikeCount,
                directCounts.SkipCount))
        {
            return CalculateAffinity(
                directCounts.LikeCount,
                directCounts.DislikeCount,
                directCounts.SkipCount);
        }

        if (aggregation.CategoryCounts.TryGetValue(
                categoryId,
                out CategoryBehaviorCounts? categoryCounts)
            && categoryCounts.DistinctInteractedPoiCount
                >= _options.MinCategoryDistinctPoiCount)
        {
            return CalculateAffinity(
                categoryCounts.LikeCount,
                categoryCounts.DislikeCount,
                categoryCounts.SkipCount);
        }

        return NeutralAffinity;
    }

    private static bool HasEvidence(int likeCount, int dislikeCount, int skipCount) =>
        (decimal)likeCount + dislikeCount + skipCount > 0m;

    private decimal CalculateAffinity(int likeCount, int dislikeCount, int skipCount)
    {
        decimal weightedSkipCount = _options.SkipWeight * skipCount;
        decimal weightedNet = likeCount - dislikeCount - weightedSkipCount;
        decimal weightedEvidence = likeCount + dislikeCount + weightedSkipCount;
        decimal raw = weightedNet / (weightedEvidence + _options.PriorWeight);
        decimal clampedRaw = Math.Clamp(raw, -1m, 1m);

        return (clampedRaw + 1m) / 2m;
    }
}