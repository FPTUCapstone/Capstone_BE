namespace TripMate.Application.Features.Scheduling.Personalization;

internal sealed class PersonalizationBaseScorer
{
    private readonly PersonalizationRankingOptions _options;

    internal PersonalizationBaseScorer(PersonalizationRankingOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    internal decimal Score(
        PersonalizationPoiFeatures features,
        decimal personalBehaviorAffinity) =>
        (_options.CategoryAffinityWeight * features.CategoryAffinity)
        + (_options.TagAffinityWeight * features.TagAffinity)
        + (_options.BehaviorAffinityWeight * personalBehaviorAffinity)
        + (_options.ScenicQualityWeight * features.ScenicQuality)
        + (_options.PhotoQualityWeight * features.PhotoQuality);
}