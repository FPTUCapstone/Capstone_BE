using TripMate.Application.Common.Models;

namespace TripMate.Application.Features.Scheduling.Personalization;

public interface IPoiRankingProvider
{
    Task<Result<PoiRankingResult>> RankAsync(
        PoiRankingRequest request,
        CancellationToken cancellationToken);
}

public sealed record PoiRankingRequest(
    IReadOnlyCollection<PoiRankingCandidate> Candidates,
    PoiRankingContext Context);

/// <param name="PreferenceTokens">The traveler's declared interests.</param>
/// <param name="Behavior">
/// UC-10 item 6: what the traveler liked and disliked so far, or null when there is no evidence. A provider
/// that does not understand it ignores it.
/// </param>
public sealed record PoiRankingContext(
    IReadOnlyCollection<string> PreferenceTokens,
    PoiRankingBehavior? Behavior = null);

public sealed record PoiRankingCandidate(
    long PoiId,
    string Name,
    string CategoryName,
    IReadOnlyCollection<string> TagNames);

/// <summary>Liked and disliked POIs, heaviest first. They may lie outside the candidate pool.</summary>
public sealed record PoiRankingBehavior(
    IReadOnlyCollection<PoiRankingBehaviorPoi> Liked,
    IReadOnlyCollection<PoiRankingBehaviorPoi> Disliked);

/// <param name="Weight">Net evidence for this POI after signal weights and time decay; always positive.</param>
public sealed record PoiRankingBehaviorPoi(
    long PoiId,
    string Name,
    string CategoryName,
    IReadOnlyCollection<string> TagNames,
    decimal Weight);

public sealed record PoiRankingResult(IReadOnlyCollection<PoiRankingItem> Ranked);

public sealed record PoiRankingItem(long PoiId, decimal AiScore, string? Reason);