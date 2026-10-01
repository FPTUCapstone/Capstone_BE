using TripMate.Application.Features.Scheduling.Common;
using TripMate.Domain.Enums;

namespace TripMate.Application.Features.Scheduling.Explanation;

public sealed record ExplanationPoiMetadata(
    string PoiName,
    string? CategoryName,
    IReadOnlyCollection<string> TagNames);

public sealed record ItineraryExplanationItem(
    int SequenceNo,
    long? PoiId,
    string? PoiName,
    string? CategoryName,
    IReadOnlyCollection<string> TagNames,
    ItineraryItemKind Kind,
    bool IsMandatory,
    DateTimeOffset PlannedArrivalUtc,
    DateTimeOffset PlannedDepartureUtc,
    int StayDurationMinutes,
    decimal? EstimatedCost,
    int? TravelDurationToNextMinutes,
    string CspRecommendationReason);

public sealed record ItineraryExplanationContext(
    IReadOnlyCollection<string> PreferenceTokens,
    DateTimeOffset StartAtUtc,
    string TimeZoneId,
    int TotalDurationMinutes);

public sealed record ItineraryExplanationInput(
    IReadOnlyCollection<ItineraryExplanationItem> Items,
    ItineraryExplanationContext Context)
{
    public static ItineraryExplanationInput Create(
        GeneratedItineraryPlan plan,
        IReadOnlyDictionary<long, ExplanationPoiMetadata> metadataByPoiId,
        IReadOnlyCollection<string> preferenceTokens,
        DateTimeOffset startAtUtc,
        string timeZoneId)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(metadataByPoiId);
        ArgumentNullException.ThrowIfNull(preferenceTokens);
        ArgumentException.ThrowIfNullOrWhiteSpace(timeZoneId);

        ItineraryExplanationItem[] items = plan.Items
            .OrderBy(item => item.SequenceNo)
            .Select(item => CreateItem(item, metadataByPoiId))
            .ToArray();
        string[] normalizedPreferenceTokens = preferenceTokens
            .Where(token => !string.IsNullOrWhiteSpace(token))
            .Select(TravelerPreferenceScoring.NormalizePreferenceToken)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();

        return new ItineraryExplanationInput(
            items,
            new ItineraryExplanationContext(
                normalizedPreferenceTokens,
                startAtUtc,
                timeZoneId.Trim(),
                plan.TotalDurationMinutes));
    }

    private static ItineraryExplanationItem CreateItem(
        GeneratedItineraryItem item,
        IReadOnlyDictionary<long, ExplanationPoiMetadata> metadataByPoiId)
    {
        ExplanationPoiMetadata? metadata = null;
        if (item.PointOfInterestId is long poiId)
        {
            metadataByPoiId.TryGetValue(poiId, out metadata);
        }

        bool hasPoi = item.PointOfInterestId.HasValue;
        string? poiName = hasPoi
            ? NormalizeOptional(metadata?.PoiName)
            : null;
        string? categoryName = hasPoi ? NormalizeOptional(metadata?.CategoryName) : null;
        string[] tagNames = hasPoi && metadata is not null
            ? NormalizeTags(metadata.TagNames)
            : [];

        return new ItineraryExplanationItem(
            item.SequenceNo,
            item.PointOfInterestId,
            poiName,
            categoryName,
            tagNames,
            item.Kind,
            item.IsMandatory,
            item.PlannedArrivalUtc,
            item.PlannedDepartureUtc,
            checked((int)(item.PlannedDepartureUtc - item.PlannedArrivalUtc).TotalMinutes),
            item.EstimatedCost,
            item.TravelDurationToNextMinutes,
            item.RecommendationReason);
    }

    private static string[] NormalizeTags(IReadOnlyCollection<string> tagNames) =>
        tagNames
            .Where(tag => !string.IsNullOrWhiteSpace(tag))
            .Select(tag => tag.Trim())
            .GroupBy(TravelerPreferenceScoring.NormalizePreferenceToken, StringComparer.Ordinal)
            .Select(group => group.First())
            .OrderBy(TravelerPreferenceScoring.NormalizePreferenceToken, StringComparer.Ordinal)
            .ToArray();

    private static string? NormalizeOptional(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}