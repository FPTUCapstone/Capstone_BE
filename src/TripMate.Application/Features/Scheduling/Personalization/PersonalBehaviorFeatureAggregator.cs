using Microsoft.EntityFrameworkCore;

using TripMate.Application.Common.Interfaces;
using TripMate.Domain.Enums;

namespace TripMate.Application.Features.Scheduling.Personalization;

public sealed record PersonalBehaviorCounts(
    int LikeCount,
    int DislikeCount,
    int SkipCount);

public sealed record CategoryBehaviorCounts(
    int LikeCount,
    int DislikeCount,
    int SkipCount,
    int DistinctInteractedPoiCount);

public sealed record PersonalBehaviorAggregation(
    IReadOnlyDictionary<long, PersonalBehaviorCounts> PoiCounts,
    IReadOnlyDictionary<int, CategoryBehaviorCounts> CategoryCounts);

public sealed class PersonalBehaviorFeatureAggregator(IApplicationDbContext dbContext)
{
    private readonly IApplicationDbContext _dbContext =
        dbContext ?? throw new ArgumentNullException(nameof(dbContext));

    public async Task<PersonalBehaviorAggregation> AggregateAsync(
        long travelerUserId,
        IReadOnlyCollection<long> candidatePoiIds,
        IReadOnlyCollection<int> relevantCategoryIds,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(candidatePoiIds);
        ArgumentNullException.ThrowIfNull(relevantCategoryIds);

        var distinctCandidatePoiIds = candidatePoiIds.Distinct().ToArray();
        var distinctCategoryIds = relevantCategoryIds.Distinct().ToArray();
        var poiCounts = new Dictionary<long, PersonalBehaviorCounts>();
        var categoryCounts = new Dictionary<int, CategoryBehaviorCounts>();

        if (distinctCandidatePoiIds.Length > 0)
        {
            var poiRows = await _dbContext.RecommendationBehaviorEvents
                .AsNoTracking()
                .Where(behaviorEvent =>
                    behaviorEvent.TravelerUserId == travelerUserId
                    && distinctCandidatePoiIds.Contains(behaviorEvent.PointOfInterestId)
                    && (behaviorEvent.EventType == RecommendationEventType.Like
                        || behaviorEvent.EventType == RecommendationEventType.Dislike
                        || behaviorEvent.EventType == RecommendationEventType.Skip))
                .GroupBy(behaviorEvent => behaviorEvent.PointOfInterestId)
                .Select(group => new
                {
                    PoiId = group.Key,
                    LikeCount = group.Count(behaviorEvent =>
                        behaviorEvent.EventType == RecommendationEventType.Like),
                    DislikeCount = group.Count(behaviorEvent =>
                        behaviorEvent.EventType == RecommendationEventType.Dislike),
                    SkipCount = group.Count(behaviorEvent =>
                        behaviorEvent.EventType == RecommendationEventType.Skip),
                })
                .ToListAsync(cancellationToken);

            poiCounts = poiRows.ToDictionary(
                row => row.PoiId,
                row => new PersonalBehaviorCounts(
                    row.LikeCount,
                    row.DislikeCount,
                    row.SkipCount));
        }

        if (distinctCategoryIds.Length > 0)
        {
            var categoryRows = await (
                from behaviorEvent in _dbContext.RecommendationBehaviorEvents.AsNoTracking()
                join poi in _dbContext.PointsOfInterest.AsNoTracking()
                    on behaviorEvent.PointOfInterestId equals poi.Id
                where behaviorEvent.TravelerUserId == travelerUserId
                    && distinctCategoryIds.Contains(poi.CategoryId)
                    && (behaviorEvent.EventType == RecommendationEventType.Like
                        || behaviorEvent.EventType == RecommendationEventType.Dislike
                        || behaviorEvent.EventType == RecommendationEventType.Skip)
                group behaviorEvent by poi.CategoryId into grouped
                select new
                {
                    CategoryId = grouped.Key,
                    LikeCount = grouped.Count(behaviorEvent =>
                        behaviorEvent.EventType == RecommendationEventType.Like),
                    DislikeCount = grouped.Count(behaviorEvent =>
                        behaviorEvent.EventType == RecommendationEventType.Dislike),
                    SkipCount = grouped.Count(behaviorEvent =>
                        behaviorEvent.EventType == RecommendationEventType.Skip),
                    DistinctInteractedPoiCount = grouped
                        .Select(behaviorEvent => behaviorEvent.PointOfInterestId)
                        .Distinct()
                        .Count(),
                }).ToListAsync(cancellationToken);

            categoryCounts = categoryRows.ToDictionary(
                row => row.CategoryId,
                row => new CategoryBehaviorCounts(
                    row.LikeCount,
                    row.DislikeCount,
                    row.SkipCount,
                    row.DistinctInteractedPoiCount));
        }

        return new PersonalBehaviorAggregation(poiCounts, categoryCounts);
    }
}