namespace TripMate.Application.Features.TripReviews.Common;

/// <summary>
/// Internal query projection, not a public API DTO. RawAverage retains SQL AVG precision.
/// Use only RawAverage in translated filters and ordering; DisplayAverage is a
/// post-materialization compatibility view using the unchanged TM-79 display helper.
/// </summary>
public sealed class TourRatingAggregateProjection
{
    public long TourId { get; init; }
    public decimal? RawAverage { get; init; }
    public int ReviewCount { get; init; }
    public decimal? DisplayAverage => TripReviewAggregateDto.Round(RawAverage);
}