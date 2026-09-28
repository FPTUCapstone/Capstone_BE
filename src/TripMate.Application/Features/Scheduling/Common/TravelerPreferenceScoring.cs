using System.Globalization;
using System.Text;
using System.Text.Json;

using TripMate.Domain.Entities;

namespace TripMate.Application.Features.Scheduling.Common;

/// <summary>
/// Produces the InterestTags ranking signal, the only traveler-profile preference consumed by
/// scheduling in TM-213. Null, empty, whitespace, and malformed JSON degrade to no tokens; input
/// length does not affect parsing. A category match adds 100 and each matching POI tag adds 100.
/// The result affects ranking only: it does not filter candidates or enforce feasibility, budget,
/// or opening hours, and it performs no AI, semantic-similarity, or CSP optimization.
/// </summary>
internal static class TravelerPreferenceScoring
{
    /// <summary>
    /// Parses normalized ordinal preference tokens, degrading unusable JSON to an empty set without
    /// applying any length-based rejection.
    /// </summary>
    internal static HashSet<string> ParsePreferenceTokens(string? interestTagsJson)
    {
        if (string.IsNullOrWhiteSpace(interestTagsJson))
        {
            return [];
        }

        try
        {
            var tags = JsonSerializer.Deserialize<string[]>(interestTagsJson) ?? [];
            return tags
                .Where(tag => !string.IsNullOrWhiteSpace(tag))
                .Select(NormalizePreferenceToken)
                .ToHashSet(StringComparer.Ordinal);
        }
        catch (JsonException)
        {
            return [];
        }
    }

    /// <summary>
    /// Calculates the ranking-only score: 100 for a category match plus 100 for each matching tag.
    /// </summary>
    internal static int CalculatePreferenceScore(
        PointOfInterest poi,
        IReadOnlySet<string> preferenceTokens)
    {
        var categoryScore = preferenceTokens.Contains(NormalizePreferenceToken(poi.Category.Name))
            ? 100
            : 0;
        var tagScore = poi.PoiTags.Count(mapping =>
            preferenceTokens.Contains(NormalizePreferenceToken(mapping.Tag.Name))) * 100;
        return categoryScore + tagScore;
    }

    private static string NormalizePreferenceToken(string value) =>
        new string(value
            .Trim()
            .Normalize(NormalizationForm.FormD)
            .Where(character => CharUnicodeInfo.GetUnicodeCategory(character)
                != UnicodeCategory.NonSpacingMark)
            .ToArray())
        .ToLowerInvariant();
}
