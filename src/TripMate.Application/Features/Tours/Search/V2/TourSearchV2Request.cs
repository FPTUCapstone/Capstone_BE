namespace TripMate.Application.Features.Tours.Search.V2;

/// <summary>Frozen UC-24 V2 criteria. Deliberately not an executable MediatR request.</summary>
public sealed record TourSearchV2Request(
    string? Keyword = null,
    string? Destination = null,
    DateOnly? StartDate = null,
    DateOnly? EndDate = null,
    long? MinPrice = null,
    long? MaxPrice = null,
    int? DurationDays = null,
    string? Category = null,
    decimal? MinimumRating = null,
    string Sort = TourSearchV2Request.SortDepartureDate,
    int Page = 1,
    int PageSize = 20)
{
    public const string SortRelevance = "relevance";
    public const string SortPriceAscending = "priceAsc";
    public const string SortPriceDescending = "priceDesc";
    public const string SortRating = "rating";
    public const string SortDepartureDate = "departureDate";
    public static readonly string[] AllowedSorts =
        [SortRelevance, SortPriceAscending, SortPriceDescending, SortRating, SortDepartureDate];

    public const string InvalidPriceRangeCode = "MSG157";
    public const string InvalidDateRangeCode = "MSG158";
    public const string InvalidPriceRangeMessage = "Minimum price must not be greater than maximum price.";
    public const string InvalidDateRangeMessage = "Start date must not be later than end date.";
}