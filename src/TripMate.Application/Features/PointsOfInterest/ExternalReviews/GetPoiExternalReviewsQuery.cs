using MediatR;

using TripMate.Application.Common.Models;

namespace TripMate.Application.Features.PointsOfInterest.ExternalReviews;

/// <summary>Tripadvisor rating and recent reviews of a POI, read live and never stored.</summary>
public sealed record GetPoiExternalReviewsQuery(long PoiId, int Size = GetPoiExternalReviewsQuery.DefaultSize)
    : IRequest<Result<PoiExternalReviewsDto>>
{
    public const int DefaultSize = 5;
    public const int MaxSize = ExternalReviewLimits.MaxReviewsPerLocation;
}

/// <summary>
/// <see cref="Source"/> is null when the POI is not linked to Tripadvisor or the service is off; clients then
/// hide the external rating. <see cref="Reviews"/> may be empty: Terra serves only a pool of recent reviews.
/// </summary>
public sealed record PoiExternalReviewsDto(
    long PoiId,
    string? Source,
    decimal? Rating,
    int ReviewCount,
    string? RatingImageUrl,
    string? Url,
    IReadOnlyList<ExternalReviewDto> Reviews,
    bool TranslationAvailable);