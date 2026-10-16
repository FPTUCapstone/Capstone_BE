using TripMate.Application.Common.Models;

namespace TripMate.Application.Features.PointsOfInterest.ExternalReviews;

/// <summary>
/// Aggregate Tripadvisor rating of one location. <c>ReviewCount</c> counts every review ever written, while
/// Tripadvisor only serves a pool of recent ones; <c>HasRecentReviews</c> says whether that pool has any to show
/// (null when not checked).
/// </summary>
public sealed record TripadvisorRating(
    long LocationId,
    decimal? Rating,
    int ReviewCount,
    string? RatingImageUrl,
    string? Url,
    bool? HasRecentReviews = null);

/// <summary>One review as Tripadvisor returns it; never stored by TripMate.</summary>
public sealed record ExternalReviewDto(
    long Id,
    int Rating,
    string? Title,
    string? Text,
    string? Language,
    DateTimeOffset? PublishedAt,
    string? TravelDate,
    string? Author,
    string? Url,
    string? RatingImageUrl);

/// <summary>
/// Reads Tripadvisor (Terra) content for linked POIs. Implementations cache results, respect a daily call
/// budget and return empty results instead of throwing when the service is disabled or unavailable.
/// </summary>
public interface ITripadvisorClient
{
    bool IsEnabled { get; }

    Task<IReadOnlyDictionary<long, TripadvisorRating>> GetRatingsAsync(
        IReadOnlyCollection<long> locationIds,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<ExternalReviewDto>> GetReviewsAsync(
        long locationId,
        int size,
        CancellationToken cancellationToken);
}

/// <summary>Translates review text on request; the original is always kept and shown first.</summary>
public interface IReviewTranslator
{
    bool IsEnabled { get; }

    Task<Result<TranslatedReview>> TranslateAsync(
        string cacheKey,
        string? title,
        string? text,
        string targetLanguage,
        CancellationToken cancellationToken);
}

public sealed record TranslatedReview(string? Title, string? Text);

/// <summary>Tripadvisor serves a pool of recent reviews; TripMate reads at most this many per location.</summary>
public static class ExternalReviewLimits
{
    public const int MaxReviewsPerLocation = 10;
}

public static class ExternalReviewErrorCodes
{
    public const string PoiNotFound = "external_reviews.poi_not_found";
    public const string ReviewNotFound = "external_reviews.review_not_found";
    public const string TranslationUnavailable = "external_reviews.translation_unavailable";
}