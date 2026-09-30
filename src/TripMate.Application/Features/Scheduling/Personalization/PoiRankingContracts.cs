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

public sealed record PoiRankingContext(IReadOnlyCollection<string> PreferenceTokens);

public sealed record PoiRankingCandidate(
    long PoiId,
    string Name,
    string CategoryName,
    IReadOnlyCollection<string> TagNames);

public sealed record PoiRankingResult(IReadOnlyCollection<PoiRankingItem> Ranked);

public sealed record PoiRankingItem(long PoiId, decimal AiScore, string? Reason);