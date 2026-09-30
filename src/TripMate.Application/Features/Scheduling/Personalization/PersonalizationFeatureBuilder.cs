using TripMate.Application.Features.Scheduling.Common;

namespace TripMate.Application.Features.Scheduling.Personalization;

internal sealed record PersonalizationPoiFeatures(
    decimal CategoryAffinity,
    decimal TagAffinity,
    decimal ScenicQuality,
    decimal PhotoQuality);

internal static class PersonalizationFeatureBuilder
{
    internal static PersonalizationPoiFeatures Build(
        IReadOnlyCollection<string> preferenceTokens,
        string categoryName,
        IReadOnlyCollection<string> tagNames,
        decimal? scenicScore,
        decimal? photoRating)
    {
        ArgumentNullException.ThrowIfNull(preferenceTokens);
        ArgumentNullException.ThrowIfNull(categoryName);
        ArgumentNullException.ThrowIfNull(tagNames);

        var normalizedPreferenceTokens = preferenceTokens.ToHashSet(StringComparer.Ordinal);
        var normalizedCategory = TravelerPreferenceScoring.NormalizePreferenceToken(categoryName);
        var categoryAffinity = normalizedPreferenceTokens.Contains(normalizedCategory)
            ? 1m
            : 0m;
        var matchingTagCount = tagNames
            .Select(TravelerPreferenceScoring.NormalizePreferenceToken)
            .Where(tag => tag.Length > 0 && tag != normalizedCategory)
            .Distinct(StringComparer.Ordinal)
            .Count(normalizedPreferenceTokens.Contains);
        var tagAffinity = Math.Min(1m, matchingTagCount / 3m);
        var scenicQuality = scenicScore.HasValue
            ? scenicScore.Value / 10m
            : 0.5m;
        var photoQuality = photoRating.HasValue
            ? photoRating.Value / 10m
            : 0.5m;

        return new PersonalizationPoiFeatures(
            categoryAffinity,
            tagAffinity,
            scenicQuality,
            photoQuality);
    }
}