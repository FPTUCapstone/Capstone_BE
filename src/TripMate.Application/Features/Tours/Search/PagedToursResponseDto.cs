namespace TripMate.Application.Features.Tours.Search;

public sealed record PagedToursResponseDto(
    int Page,
    int PageSize,
    long TotalCount,
    long TotalPages,
    DateTime AsOfUtc,
    IReadOnlyList<TourSearchItemDto> Items);