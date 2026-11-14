using TripMate.Domain.Common;

namespace TripMate.Domain.Entities;

/// <summary>The score produced for one candidate Tour in a search-request snapshot.</summary>
public sealed class TourRecommendationLog : BaseEntity
{
    public const decimal MinimumEffectiveThreshold = 80m;

    private TourRecommendationLog()
    {
    }

    public long SearchRequestId { get; private set; }

    public TourSearchRequest SearchRequest { get; private set; } = null!;

    public long TourId { get; private set; }

    public Tour Tour { get; private set; } = null!;

    public decimal SimilarityScore { get; private set; }

    public bool IsRecommended { get; private set; }

    public DateTimeOffset RecommendedAtUtc { get; private set; }

    internal static TourRecommendationLog Create(
        TourSearchRequest searchRequest,
        long tourId,
        decimal similarityScore,
        decimal configuredThreshold,
        DateTimeOffset recommendedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(searchRequest);
        return Create(searchRequest.Id, searchRequest, tourId, similarityScore, configuredThreshold, recommendedAtUtc);
    }

    public static TourRecommendationLog CreateForPersistedSnapshot(
        long searchRequestId,
        long tourId,
        decimal similarityScore,
        decimal configuredThreshold,
        DateTimeOffset recommendedAtUtc)
    {
        if (searchRequestId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(searchRequestId));
        }

        return Create(searchRequestId, null, tourId, similarityScore, configuredThreshold, recommendedAtUtc);
    }

    private static TourRecommendationLog Create(
        long searchRequestId,
        TourSearchRequest? searchRequest,
        long tourId,
        decimal similarityScore,
        decimal configuredThreshold,
        DateTimeOffset recommendedAtUtc)
    {
        if (tourId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(tourId));
        }

        if (similarityScore is < 0 or > 100)
        {
            throw new ArgumentOutOfRangeException(nameof(similarityScore));
        }

        decimal effectiveThreshold = Math.Max(configuredThreshold, MinimumEffectiveThreshold);
        return new TourRecommendationLog
        {
            SearchRequestId = searchRequestId,
            SearchRequest = searchRequest!,
            TourId = tourId,
            SimilarityScore = similarityScore,
            IsRecommended = similarityScore > effectiveThreshold,
            RecommendedAtUtc = recommendedAtUtc.ToUniversalTime(),
        };
    }
}
