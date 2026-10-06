using System.Collections.Frozen;

namespace TripMate.Application.Features.Scheduling.Personalization;

internal enum PoiRankingOutcomeCategory
{
    Success,
    ProviderSkipped,
    Timeout,
    Quota,
    Network,
    ServerError,
    InvalidResponse,
}

internal sealed record PoiRankingSnapshot(
    FrozenSet<long> ProviderPoolPoiIds,
    FrozenDictionary<long, PoiRankingSnapshotEntry> Entries,
    PoiRankingOutcomeCategory OutcomeCategory,
    PersonalBehaviorAggregation BehaviorAggregation);

internal sealed record PoiRankingSnapshotEntry(
    long PoiId,
    decimal TripMateBaseScore,
    decimal EffectiveDesirabilityScore,
    decimal? ScenicScoreForRanking,
    decimal? PhotoRatingForRanking,
    decimal ExplorationDistanceForRanking,
    decimal? EstimatedVisitCostForRanking);