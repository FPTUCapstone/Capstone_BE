using System.Security.Cryptography;
using System.Text.Json;

using TripMate.Application.Features.Scheduling.Personalization;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Application.Features.Scheduling.Common;

internal sealed record SchedulingGenerationSnapshot(string Hash)
{
    public static SchedulingGenerationSnapshot Capture(
        string? travelerInterestTagsJson,
        IReadOnlyCollection<PointOfInterest> pointsOfInterest,
        PersonalBehaviorAggregation? behaviorAggregation = null) =>
        Create(CaptureData(travelerInterestTagsJson, pointsOfInterest) with
        {
            Behavior = ToBehaviorSnapshot(behaviorAggregation),
        });

    internal static SchedulingGenerationSnapshotData CaptureData(
        string? travelerInterestTagsJson,
        IReadOnlyCollection<PointOfInterest> pointsOfInterest) =>
        new(
            TravelerPreferenceScoring.ParsePreferenceTokens(travelerInterestTagsJson)
                .Order(StringComparer.Ordinal)
                .ToArray(),
            pointsOfInterest.Select(ToSnapshotPoi).ToArray(),
            []);

    internal static SchedulingGenerationSnapshot Capture(
        SchedulingGenerationSnapshotData data,
        PersonalBehaviorAggregation behaviorAggregation) =>
        Create(data with { Behavior = ToBehaviorSnapshot(behaviorAggregation) });

    internal static SchedulingGenerationSnapshot Create(
        SchedulingGenerationSnapshotData data)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(data.Pois);

        var canonical = data with
        {
            Pois = data.Pois
                .OrderBy(poi => poi.PoiId)
                .Select(poi => poi with
                {
                    VerifiedAtUtc = poi.VerifiedAtUtc?.ToUniversalTime(),
                    OpeningHours = poi.OpeningHours
                        .OrderBy(hours => hours.DayOfWeek)
                        .ThenBy(hours => hours.OpenTime)
                        .ThenBy(hours => hours.CloseTime)
                        .ThenBy(hours => hours.IsClosed)
                        .ToArray(),
                    Tags = poi.Tags
                        .OrderBy(tag => tag.TagId)
                        .ThenBy(tag => tag.Name, StringComparer.Ordinal)
                        .ToArray(),
                })
                .ToArray(),
            PreferenceTokens = data.PreferenceTokens
                .Order(StringComparer.Ordinal)
                .Distinct(StringComparer.Ordinal)
                .ToArray(),
            Behavior = data.Behavior
                .OrderBy(item => item.Scope)
                .ThenBy(item => item.Id)
                .ToArray(),
        };
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(canonical);
        return new SchedulingGenerationSnapshot(
            Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant());
    }

    private static IReadOnlyCollection<SchedulingGenerationSnapshotBehavior> ToBehaviorSnapshot(
        PersonalBehaviorAggregation? aggregation)
    {
        if (aggregation is null)
        {
            return [];
        }

        return aggregation.PoiCounts.Select(pair => new SchedulingGenerationSnapshotBehavior(
                BehaviorScope.Poi,
                pair.Key,
                pair.Value.LikeCount,
                pair.Value.DislikeCount,
                pair.Value.SkipCount,
                0))
            .Concat(aggregation.CategoryCounts.Select(pair =>
                new SchedulingGenerationSnapshotBehavior(
                    BehaviorScope.Category,
                    pair.Key,
                    pair.Value.LikeCount,
                    pair.Value.DislikeCount,
                    pair.Value.SkipCount,
                    pair.Value.DistinctInteractedPoiCount)))
            .ToArray();
    }

    private static SchedulingGenerationSnapshotPoi ToSnapshotPoi(PointOfInterest poi)
    {
        ArgumentNullException.ThrowIfNull(poi);
        return new SchedulingGenerationSnapshotPoi(
            poi.Id,
            poi.Status,
            poi.Name,
            poi.CategoryId,
            poi.Category.Name,
            poi.Latitude,
            poi.Longitude,
            poi.AverageVisitDurationMinutes,
            poi.EstimatedVisitCost,
            poi.ScenicScore,
            poi.PhotoRating,
            poi.HasShelter,
            poi.SourceUrl,
            poi.VerifiedAtUtc,
            poi.OpeningHours.Select(hours => new SchedulingGenerationSnapshotOpeningHour(
                hours.DayOfWeek,
                hours.OpenTime,
                hours.CloseTime,
                hours.IsClosed)).ToArray(),
            poi.PoiTags.Select(mapping => new SchedulingGenerationSnapshotTag(
                mapping.TagId,
                mapping.Tag.Name)).ToArray());
    }
}

internal sealed record SchedulingGenerationSnapshotData(
    IReadOnlyCollection<string> PreferenceTokens,
    IReadOnlyCollection<SchedulingGenerationSnapshotPoi> Pois,
    IReadOnlyCollection<SchedulingGenerationSnapshotBehavior> Behavior);

internal enum BehaviorScope
{
    Poi,
    Category,
}

internal sealed record SchedulingGenerationSnapshotBehavior(
    BehaviorScope Scope,
    long Id,
    int LikeCount,
    int DislikeCount,
    int SkipCount,
    int DistinctInteractedPoiCount);

internal sealed record SchedulingGenerationSnapshotPoi(
    long PoiId,
    PointOfInterestStatus Status,
    string Name,
    int CategoryId,
    string CategoryName,
    decimal Latitude,
    decimal Longitude,
    int AverageVisitDurationMinutes,
    decimal? EstimatedVisitCost,
    decimal? ScenicScore,
    decimal? PhotoRating,
    bool HasShelter,
    string? SourceUrl,
    DateTimeOffset? VerifiedAtUtc,
    IReadOnlyCollection<SchedulingGenerationSnapshotOpeningHour> OpeningHours,
    IReadOnlyCollection<SchedulingGenerationSnapshotTag> Tags);

internal sealed record SchedulingGenerationSnapshotOpeningHour(
    byte DayOfWeek,
    TimeOnly? OpenTime,
    TimeOnly? CloseTime,
    bool IsClosed);

internal sealed record SchedulingGenerationSnapshotTag(int TagId, string Name);