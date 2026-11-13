using MediatR;

using TripMate.Application.Common.Models;

namespace TripMate.Application.Features.Tours.Search;

public sealed record SearchToursQuery(
    string? Destination = null,
    DateOnly? DepartureDate = null,
    long? MinPrice = null,
    long? MaxPrice = null,
    int Page = 1,
    int PageSize = 20,
    string? Keyword = null,
    DateOnly? StartDate = null,
    DateOnly? EndDate = null,
    int? DurationDays = null,
    string? Category = null,
    decimal? MinimumRating = null,
    string? Sort = null) : IRequest<Result<PagedToursResponseDto>>
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
}