namespace TripMate.Application.Features.TripReviews.Common;

public sealed record TripReviewAggregateDto(decimal? Average, int Count)
{
    public static TripReviewAggregateDto Empty { get; } = new(null, 0);

    public static decimal? Round(decimal? value) => value.HasValue
        ? Math.Round(value.Value, 1, MidpointRounding.AwayFromZero)
        : null;
}